using Arrkit.Library;
using Microsoft.Extensions.Options;
using Ripparr.Arr;
using Ripparr.Options;

namespace Ripparr.Jobs;

/// <summary>The finished extras of one film or series that are not in the library yet.</summary>
public sealed record ExtrasGroup(string Key, string Title, int? Year, bool Tv, int? TmdbId, int? TvdbId, IReadOnlyList<Job> Jobs)
{
    public string Display => Year is { } y ? $"{Title} ({y})" : Title;
}

/// <summary>Where a group's film or series folder is, and how it was found (or why it wasn't).</summary>
public sealed record FolderLookup(string? Path, string How);

/// <summary>
/// Copies finished extras (done/Title (Year)/featurettes/...) into the film's or series' folder in the library, which
/// Radarr's and Sonarr's manual import leave behind. The folder comes from Radarr or Sonarr by TMDb/TVDB ID, else by
/// "Title (Year)" on disk, or the one picked on the Extras page. The copy itself is arrkit's: read back and compared,
/// never overwriting. One copy runs at a time; with Auto copy on, extras go as soon as their film is imported.
/// </summary>
public sealed class ExtrasLibrary(
    JobStore store,
    LiveSettings settings,
    RadarrClient radarr,
    SonarrClient sonarr,
    IOptions<RipparrOptions> options,
    ILogger<ExtrasLibrary> logger) : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan AutoEvery = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly SemaphoreSlim _poke = new(0, 1);
    private readonly Dictionary<string, ExtrasCopyResult> _results = [];

    /// <summary>A copy started, moved or ended.</summary>
    public event Action? Changed;

    /// <summary>The group being copied now, and how far it is (0 to 100).</summary>
    public string? Running { get; private set; }

    public int Percent { get; private set; }

    public ExtrasOptions Options => settings.Extras;

    /// <summary>How the last copy of a group went, until the app restarts.</summary>
    public ExtrasCopyResult? LastResult(string key)
    {
        lock (_results)
        {
            return _results.GetValueOrDefault(key);
        }
    }

    /// <summary>Done extras (not dry runs) still in the done folder and not in the library, by film or series.</summary>
    public List<ExtrasGroup> Waiting() => Group(store.List(5000, [JobStatus.Done])
        .Where(j => j is { IsExtra: true, DryRun: false, OutputPath: not null } && File.Exists(j.OutputPath)
                    && (j.LibraryPath == null || !File.Exists(j.LibraryPath))));

    /// <summary>Extras copied to the library, newest first.</summary>
    public List<Job> Copied(int limit = 50) => store.List(5000, [JobStatus.Done])
        .Where(j => j is { IsExtra: true, LibraryPath: not null })
        .OrderByDescending(j => j.Finished)
        .Take(limit)
        .ToList();

    public static List<ExtrasGroup> Group(IEnumerable<Job> jobs) => jobs
        .GroupBy(Key)
        .Select(g =>
        {
            var first = g.First();
            return new ExtrasGroup(g.Key, first.Title, first.Year, first.TvdbId != null, first.TmdbId, first.TvdbId,
                g.OrderBy(j => j.Season ?? -1).ThenBy(j => j.ExtraType).ThenBy(j => j.ExtraName).ToList());
        })
        .OrderBy(g => g.Display, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>"tv:78804", "movie:949", or the title and year for a job with no ID.</summary>
    public static string Key(Job job) => job.TvdbId is { } tvdb ? $"tv:{tvdb}"
        : job.TmdbId is { } tmdb ? $"movie:{tmdb}"
        : $"name:{job.Title.ToLowerInvariant()}:{job.Year}";

    private static string ChosenKey(string groupKey) => $"extras.folder.{groupKey}";

    /// <summary>Remembers the folder picked by hand for a group (null forgets it).</summary>
    public void Choose(ExtrasGroup group, string? folder)
    {
        store.SetSetting(ChosenKey(group.Key), folder);
        Raise();
    }

    /// <summary>Every film or series folder of the group's kind, for picking one by hand.</summary>
    public List<LibraryFolder> Folders(bool tv) => LibraryFolders.List(Options.Roots).Where(f => f.Tv == tv).ToList();

    /// <summary>Finds the group's folder: picked by hand, then Radarr/Sonarr, then "Title (Year)" on disk.</summary>
    public async Task<FolderLookup> FindAsync(ExtrasGroup group, CancellationToken cancellationToken)
    {
        var roots = Options.Roots;
        if (store.Setting(ChosenKey(group.Key)) is { } chosen && Usable(roots, chosen))
        {
            return new FolderLookup(chosen, "picked by hand");
        }

        string? arrNote = null;
        if (group.Tv && group.TvdbId is { } tvdb && sonarr.Configured)
        {
            var (matches, error) = await sonarr.SearchAsync($"tvdb:{tvdb}", cancellationToken);
            var path = matches.FirstOrDefault(m => m.TvdbId == tvdb)?.LibraryPath;
            if (path != null && Usable(roots, path))
            {
                return new FolderLookup(path, "from Sonarr");
            }

            arrNote = error ?? (path == null ? "not in Sonarr yet" : $"Sonarr's folder {path} is not in {Options.TvFolder} here");
        }
        else if (!group.Tv && group.TmdbId is { } tmdb && radarr.Configured)
        {
            var (matches, error) = await radarr.SearchAsync($"tmdb:{tmdb}", cancellationToken);
            var path = matches.FirstOrDefault(m => m.TmdbId == tmdb)?.LibraryPath;
            if (path != null && Usable(roots, path))
            {
                return new FolderLookup(path, "from Radarr");
            }

            arrNote = error ?? (path == null ? "not in Radarr yet" : $"Radarr's folder {path} is not in {Options.MoviesFolder} here");
        }

        var found = LibraryFolders.Find(LibraryFolders.List(roots), group.Title, group.Year, group.Tv);
        if (found != null)
        {
            return new FolderLookup(found.Path, arrNote == null ? "found by name" : $"found by name ({arrNote})");
        }

        return new FolderLookup(null, (arrNote != null ? char.ToUpperInvariant(arrNote[0]) + arrNote[1..] + ", and no" : "No")
                                      + $" {group.Display} folder in {(group.Tv ? Options.TvFolder : Options.MoviesFolder)}. "
                                      + "Import it first, or pick the folder.");
    }

    /// <summary>Is it a film or series folder that exists (not a root or a category)?</summary>
    private static bool Usable(IReadOnlyList<LibraryRoot> roots, string path) =>
        Directory.Exists(path) && LibraryFolders.IsTitleFolder(roots, path);

    /// <summary>Where each extra of the group would go in the folder; reads the disk only.</summary>
    public List<ExtraStep> Plan(ExtrasGroup group, string folder) =>
        ExtrasPlan.Make(Options.Roots, folder, group.Jobs.Select(j =>
            new ExtraFile(j.OutputPath!, j.ExtraType!, Path.GetFileNameWithoutExtension(j.OutputPath!), j.Season)));

    /// <summary>Copies the group's extras into the folder. Waits for a copy already running. Returns how it went.</summary>
    public async Task<ExtrasCopyResult> CopyAsync(ExtrasGroup group, string folder, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        var extras = Options;
        try
        {
            Running = group.Key;
            Percent = 0;
            Raise();
            var steps = Plan(group, folder);
            logger.LogInformation("Copying {Count} extras of {Title} to {Folder}", steps.Count(s => s.ToCopy), group.Display, folder);
            var last = DateTime.MinValue;
            var result = await ExtrasCopier.CopyAsync(steps, extras.Verify, (done, total) =>
            {
                Percent = total == 0 ? 100 : (int)(done * 100 / total);
                if ((DateTime.UtcNow - last).TotalMilliseconds > 500)
                {
                    last = DateTime.UtcNow;
                    Raise();
                }
            }, line => logger.LogInformation("{Line}", line), cancellationToken);

            foreach (var (step, target) in result.Placed)
            {
                if (group.Jobs.FirstOrDefault(j => j.OutputPath == step.File.Source) is not { } job)
                {
                    continue;
                }

                job.LibraryPath = target;
                store.Update(job);
                if (extras.RemoveDoneCopy)
                {
                    RemoveDoneCopy(step.File.Source);
                }
            }

            foreach (var (step, error) in result.Failed)
            {
                logger.LogWarning("Extra {Source} not copied: {Error}", step.File.Source, error);
            }

            var notifications = settings.Notifications;
            if (extras.TellJellyfin && notifications.JellyfinConfigured && result.Copied > 0
                && await JellyfinScan.FilesAddedAsync(Http, notifications.JellyfinUrl, notifications.JellyfinApiKey,
                    result.Targets, cancellationToken) is { } jellyfinError)
            {
                logger.LogWarning("Jellyfin not told about the new extras: {Error}", jellyfinError);
            }

            logger.LogInformation("{Title}: {Summary}", group.Display, result.Summary);
            lock (_results)
            {
                _results[group.Key] = result;
            }

            return result;
        }
        finally
        {
            Running = null;
            _one.Release();
            Raise();
        }
    }

    /// <summary>Says a job ended: an extra may be ready to copy (with Auto copy on).</summary>
    public void Poke(Job job)
    {
        if (job is { Status: JobStatus.Done, IsExtra: true, DryRun: false } && _poke.CurrentCount == 0)
        {
            _poke.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _poke.WaitAsync(AutoEvery, stoppingToken);
                if (Options.AutoCopy)
                {
                    await AutoCopyAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Auto copy of extras failed");
            }
        }
    }

    /// <summary>Copies every waiting group whose folder is found and whose extras can all go.</summary>
    private async Task AutoCopyAsync(CancellationToken cancellationToken)
    {
        foreach (var group in Waiting())
        {
            var lookup = await FindAsync(group, cancellationToken);
            if (lookup.Path == null)
            {
                continue;
            }

            if (Plan(group, lookup.Path).Any(s => s.State == ExtraStepState.Problem))
            {
                logger.LogInformation("Auto copy skipped {Title}: an extra can't be copied, see the Extras page", group.Display);
                continue;
            }

            await CopyAsync(group, lookup.Path, cancellationToken);
        }
    }

    /// <summary>Deletes a done file (only inside the done folder) and the folders it leaves empty.</summary>
    private void RemoveDoneCopy(string path)
    {
        var done = LibraryFolders.Clean(options.Value.DoneFolder);
        if (!LibraryFolders.Clean(path).StartsWith(done + "/", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            File.Delete(path);
            for (var folder = Path.GetDirectoryName(path); folder != null && LibraryFolders.Clean(folder) != done
                                                                         && LibraryFolders.Clean(folder).StartsWith(done + "/", StringComparison.Ordinal)
                                                                         && !Directory.EnumerateFileSystemEntries(folder).Any();
                 folder = Path.GetDirectoryName(folder))
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove the done copy {Path}: {Message}", path, e.Message);
        }
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A page that went away must not stop the copy.
        }
    }
}
