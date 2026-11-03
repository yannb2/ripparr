using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ripparr.Options;

namespace Ripparr.Disc;

/// <summary>Reads a source's playlists, clip sizes and encryption. Results are kept in memory per path and date.</summary>
public sealed partial class DiscAnalyzer(IOptions<RipparrOptions> options, ILogger<DiscAnalyzer> logger)
{
    private readonly RipparrOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, (DateTime Modified, DiscAnalysis Analysis)> _cache = new();

    public TimeSpan Minimum => TimeSpan.FromSeconds(_options.MinPlaylistSeconds);

    /// <summary>Finds a source by its path, if it is under one of the roots.</summary>
    public DiscSource? Describe(string path)
    {
        var full = Path.GetFullPath(path);
        var root = _options.Sources.FirstOrDefault(r => IsUnder(full, r.Path));
        var rootPath = root?.Path ?? Path.GetDirectoryName(full) ?? "/";
        if (File.Exists(full) && full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            var info = new FileInfo(full);
            return new DiscSource(full, SourceKind.Iso, rootPath, info.Length, info.LastWriteTimeUtc);
        }

        if (Directory.Exists(full) && SourceFinder.BdmvParent(full) is { } parent)
        {
            return SourceFinder.Find([new SourceRoot { Path = parent }], [])
                .Select(s => s with { Root = rootPath })
                .FirstOrDefault();
        }

        return null;
    }

    public async Task<DiscAnalysis> AnalyseAsync(DiscSource source, CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(source.Path, out var cached) && cached.Modified == source.Modified)
        {
            return cached.Analysis;
        }

        var analysis = new DiscAnalysis { Source = source };
        try
        {
            if (source.Kind == SourceKind.Iso)
            {
                await ReadIsoAsync(analysis, cancellationToken);
            }
            else
            {
                ReadFolder(analysis);
            }

            analysis.Classify(Minimum);
            if (source.Kind == SourceKind.Folder && analysis.MainFeature is { } main && main.Mpls.Clips.Count > 0)
            {
                var clip = ClipPath(source.Path, main.Mpls.Clips[0]);
                analysis.Encryption = clip == null ? Encryption.Unknown : EncryptionCheck.Check(clip);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not read {Path}", source.Path);
            analysis.Error = e.Message;
        }

        if (analysis.Playlists.Count == 0 && analysis.Error == null)
        {
            analysis.Error = "No playlists found in BDMV/PLAYLIST.";
        }

        _cache[source.Path] = (source.Modified, analysis);
        return analysis;
    }

    /// <summary>The .m2ts file of a clip in a folder source (BDMV/STREAM/00055.m2ts).</summary>
    public static string? ClipPath(string folder, string clip)
    {
        var bdmv = SourceFinder.DirIn(folder, "BDMV");
        var stream = bdmv == null ? null : SourceFinder.DirIn(bdmv, "STREAM");
        return stream == null ? null : SourceFinder.FileIn(stream, clip + ".m2ts");
    }

    /// <summary>The .mpls file of a playlist in a folder source.</summary>
    public static string? PlaylistPath(string folder, string playlist)
    {
        var bdmv = SourceFinder.DirIn(folder, "BDMV");
        var playlists = bdmv == null ? null : SourceFinder.DirIn(bdmv, "PLAYLIST");
        return playlists == null ? null : SourceFinder.FileIn(playlists, playlist + ".mpls");
    }

    private void ReadFolder(DiscAnalysis analysis)
    {
        var folder = analysis.Source.Path;
        var bdmv = SourceFinder.DirIn(folder, "BDMV") ?? throw new InvalidDataException("No BDMV folder.");
        analysis.HasAacsFolder = SourceFinder.DirIn(folder, "AACS") != null;
        ReadPlaylists(analysis, SourceFinder.DirIn(bdmv, "PLAYLIST"));

        var stream = SourceFinder.DirIn(bdmv, "STREAM");
        if (stream != null)
        {
            foreach (var file in new DirectoryInfo(stream).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (file.Extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.ClipSizes[Path.GetFileNameWithoutExtension(file.Name)] = file.Length;
                }
            }
        }

        analysis.DiscTitle = ReadDiscTitle(bdmv);
    }

    private async Task ReadIsoAsync(DiscAnalysis analysis, CancellationToken cancellationToken)
    {
        var reader = new IsoReader(_options.Tools.SevenZip);
        var (entries, error) = await reader.ListAsync(analysis.Source.Path, cancellationToken);
        if (entries == null)
        {
            analysis.Error = error;
            return;
        }

        analysis.HasAacsFolder = entries.Any(e => e.Path.StartsWith("AACS", StringComparison.OrdinalIgnoreCase));
        analysis.Encryption = analysis.HasAacsFolder ? Encryption.Likely : Encryption.None;
        foreach (var entry in entries.Where(e => !e.Folder))
        {
            var parts = entry.Path.Split('/');
            if (parts.Length == 3 && parts[0].Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                                  && parts[1].Equals("STREAM", StringComparison.OrdinalIgnoreCase)
                                  && parts[2].EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase))
            {
                analysis.ClipSizes[Path.GetFileNameWithoutExtension(parts[2])] = entry.Size;
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}");
        try
        {
            var result = await reader.ExtractAsync(analysis.Source.Path, temp,
                ["BDMV/PLAYLIST/*", "BDMV/META/DL/*"], cancellationToken);
            if (!result.Success)
            {
                analysis.Error = $"7-Zip could not unpack the playlists: {result.LastLine}";
                return;
            }

            var bdmv = SourceFinder.DirIn(temp, "BDMV");
            ReadPlaylists(analysis, bdmv == null ? null : SourceFinder.DirIn(bdmv, "PLAYLIST"));
            analysis.DiscTitle = bdmv == null ? null : ReadDiscTitle(bdmv);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void ReadPlaylists(DiscAnalysis analysis, string? folder)
    {
        if (folder == null)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                     .Where(f => f.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                analysis.Playlists.Add(new PlaylistInfo { Name = Path.GetFileNameWithoutExtension(file), Mpls = Mpls.Read(file) });
            }
            catch (InvalidDataException e)
            {
                analysis.Notes.Add($"{Path.GetFileName(file)} could not be read: {e.Message}");
            }
        }
    }

    /// <summary>The disc's own name from BDMV/META/DL/bdmt_eng.xml (di:name).</summary>
    internal static string? ReadDiscTitle(string bdmv)
    {
        var meta = SourceFinder.DirIn(bdmv, "META");
        var dl = meta == null ? null : SourceFinder.DirIn(meta, "DL");
        if (dl == null)
        {
            return null;
        }

        var file = SourceFinder.FileIn(dl, "bdmt_eng.xml")
                   ?? Directory.EnumerateFiles(dl, "bdmt_*.xml").FirstOrDefault();
        if (file == null)
        {
            return null;
        }

        var match = DiscNameRegex().Match(File.ReadAllText(file));
        if (!match.Success)
        {
            return null;
        }

        var name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return name.Length == 0 || name.Equals("Blu-ray", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>Whether a path is inside one of the configured source folders.</summary>
    public bool InSources(string path) => _options.Sources.Any(r => IsUnder(Path.GetFullPath(path), r.Path));

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(root).TrimEnd('/') + "/";
        return path.StartsWith(full, StringComparison.Ordinal) || path == full.TrimEnd('/');
    }

    [GeneratedRegex(@"<di:name>([^<]*)</di:name>", RegexOptions.IgnoreCase)]
    private static partial Regex DiscNameRegex();
}
