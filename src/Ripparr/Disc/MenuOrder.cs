using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ripparr.Jobs;
using Ripparr.Options;

namespace Ripparr.Disc;

/// <summary>
/// A Java (BD-J) menu's playlists, read without running it: its JAR files are zips of class and
/// config files, and discs name their playlists in them ("00117", "PLAYLIST:00117",
/// &lt;playlist&gt;117&lt;/playlist&gt;). The file that names most of the episodes gives their order, as
/// they first appear in it. A guess: a menu that works the numbers out in code names none.
/// </summary>
public static partial class JarScan
{
    public static (List<string>? Order, string? File) Order(IEnumerable<string> jars, IReadOnlyCollection<string> playlists)
    {
        var wanted = playlists.Where(n => n.Length == 5 && n.All(char.IsAsciiDigit)).ToHashSet();
        (List<string> Order, string File)? best = null;
        foreach (var jar in jars)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                foreach (var entry in zip.Entries.Where(e => e.Length is > 0 and < 4 * 1024 * 1024))
                {
                    using var stream = entry.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    var order = InText(Encoding.Latin1.GetString(memory.ToArray()), wanted);
                    if (order.Count >= 2 && (best == null || order.Count > best.Value.Order.Count))
                    {
                        best = (order, $"{Path.GetFileName(jar)}/{entry.FullName}");
                    }
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // not a zip, or a broken one: try the next
            }
        }

        return best == null ? (null, null) : (best.Value.Order, best.Value.File);
    }

    /// <summary>The wanted playlists in the order the text first names them.</summary>
    public static List<string> InText(string text, IReadOnlySet<string> wanted)
    {
        var order = new List<string>();
        foreach (Match match in NumberRegex().Matches(text))
        {
            var name = match.Groups["five"].Success
                ? match.Groups["five"].Value
                : int.Parse(match.Groups["short"].Value).ToString("00000");
            if (wanted.Contains(name) && !order.Contains(name))
            {
                order.Add(name);
            }
        }

        return order;
    }

    // Five digits on their own ("00117", "PLAYLIST:00117", "00117.mpls"), or a shorter number right after "playlist".
    [GeneratedRegex(@"(?<![0-9])(?<five>[0-9]{5})(?![0-9])|playlist[^0-9a-z]{0,12}(?<short>[0-9]{1,4})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NumberRegex();
}

/// <summary>
/// Reads the order a disc's menu shows its episodes in: the HDMV menu's buttons (the interactive
/// graphics stream of a menu clip), else the Java menu's JAR files. Read once per disc, on demand.
/// </summary>
public sealed class MenuReader(IOptions<RipparrOptions> options, JobStore store, ILogger<MenuReader> logger)
{
    /// <summary>A menu's buttons are at the start of its clip: this much of each clip is read.</summary>
    public const long ReadBytes = 16L * 1024 * 1024;

    /// <summary>Clips looked at, smallest first (menus are small), and the biggest one looked at.</summary>
    public const int MaxClips = 30;

    public const long MaxClipBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>How a note on an order read from the Java menu starts.</summary>
    public const string JavaNote = "in the order the Java menu names them";

    public async Task ReadAsync(DiscSource source, DiscAnalysis analysis, IReadOnlyCollection<PlaylistInfo> episodes, CancellationToken cancellationToken)
    {
        if (analysis.IsDvd || episodes.Count < 2)
        {
            return;
        }

        var names = episodes.Select(e => e.Name).ToList();
        var skip = episodes.SelectMany(e => e.Mpls.Clips).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clips = analysis.ClipSizes
            .Where(c => !skip.Contains(c.Key) && c.Value <= MaxClipBytes)
            .OrderBy(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(MaxClips).Select(c => c.Key).ToList();
        (List<string>? Order, string? Note) result;
        try
        {
            if (source.Kind == SourceKind.Iso)
            {
                var running = store.List(50, [JobStatus.Running]).Where(j => j.SourcePath == source.Path).ToList();
                var mounted = await IsoMount.BorrowAsync(options.Value.IsoMount, source.Path, running.FirstOrDefault(j => j.MountedAt != null)?.MountedAt,
                    () => store.List(50, [JobStatus.Running]).Any(j => j.SourcePath == source.Path),
                    disc =>
                    {
                        var (order, note) = FromFolder(disc, clips, names);
                        return Task.FromResult(new Tuple<List<string>?, string?>(order, note));
                    });
                result = mounted != null ? (mounted.Item1, mounted.Item2) : await FromIsoAsync(source, clips, names, cancellationToken);
            }
            else
            {
                result = FromFolder(SourceFinder.BdmvParent(source.Path) ?? source.Path, clips, names);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(e, "Reading the menu of {Path} failed", source.Path);
            result = (null, $"Could not read the disc menu: {e.Message}");
        }

        analysis.MenuOrder = result.Order;
        analysis.MenuNote = result.Note;
        analysis.MenuIsGuess = result.Order != null && result.Note?.StartsWith(JavaNote, StringComparison.Ordinal) == true;
    }

    /// <summary>A folder (or a mounted ISO): the menu clips' buttons, else the JAR files.</summary>
    public static (List<string>? Order, string? Note) FromFolder(string disc, IReadOnlyList<string> clips, IReadOnlyCollection<string> episodes)
    {
        foreach (var clip in clips)
        {
            if (DiscAnalyzer.ClipPath(disc, clip) is not { } path)
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            if (IgMenu.Order(IgMenu.Read(stream, ReadBytes), episodes) is { } order)
            {
                return (order, $"in the order of the disc menu's buttons (clip {clip})");
            }
        }

        var bdmv = SourceFinder.DirIn(disc, "BDMV");
        var jarFolder = bdmv == null ? null : SourceFinder.DirIn(bdmv, "JAR");
        if (jarFolder != null)
        {
            var jars = Directory.EnumerateFiles(jarFolder, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
            var (order, file) = JarScan.Order(jars, episodes);
            return order != null
                ? (order, $"{JavaNote} ({file}); a guess, check with Watch")
                : (null, "The disc's Java menu names no episode playlist the way Ripparr can read.");
        }

        return (null, "The disc menu plays none of these playlists directly.");
    }

    // No mount service: 7-Zip streams the start of each candidate clip, then unpacks BDMV/JAR.
    private async Task<(List<string>? Order, string? Note)> FromIsoAsync(DiscSource source, IReadOnlyList<string> clips,
        IReadOnlyCollection<string> episodes, CancellationToken cancellationToken)
    {
        var sevenZip = options.Value.Tools.SevenZip;
        foreach (var clip in clips)
        {
            var start = new ProcessStartInfo(sevenZip) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "e", "-so", source.Path, $"BDMV/STREAM/{clip}.m2ts" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("7-Zip did not start.");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            try
            {
                var buttons = await Task.Run(() => IgMenu.Read(process.StandardOutput.BaseStream, ReadBytes), cancellationToken);
                if (IgMenu.Order(buttons, episodes) is { } order)
                {
                    return (order, $"in the order of the disc menu's buttons (clip {clip})");
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
        }

        var folder = Path.Combine(Path.GetTempPath(), "ripparr-menu-" + Guid.NewGuid().ToString("N"));
        try
        {
            var unpacked = await new IsoReader(sevenZip).ExtractAsync(source.Path, folder, ["BDMV/JAR/*"], cancellationToken, timeout: TimeSpan.FromMinutes(2));
            var jars = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList()
                : [];
            if (!unpacked.Success || jars.Count == 0)
            {
                return (null, "The disc menu plays none of these playlists directly.");
            }

            var (order, file) = JarScan.Order(jars, episodes);
            return order != null
                ? (order, $"{JavaNote} ({file}); a guess, check with Watch")
                : (null, "The disc's Java menu names no episode playlist the way Ripparr can read.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
