using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Options;
using Ripparr.Jobs;
using Ripparr.Options;
using Ripparr.Tools;

namespace Ripparr.Disc;

/// <summary>
/// Finds the film on a disc that hides it among fake playlists, the two ways people do it by hand
/// (MakeMKV forum, "Deobfuscate playlists automatically", GetMPLS):
/// <list type="bullet">
/// <item>Clip joins: the film's clips follow each other seamlessly, so the last picture of one
/// clip and the first of the next look alike; a fake's shuffled order jumps. Each playlist gets
/// the mean picture change over its joins, and the smallest wins when it stands clearly apart.</item>
/// <item>MakeMKV: it plays the disc's menus (BD-J too) and calls one title the main feature.</item>
/// </list>
/// </summary>
public sealed class PlaylistFinder(IOptions<RipparrOptions> options, JobStore store, ILogger<PlaylistFinder> logger)
{
    /// <summary>Pictures are compared this small and in grey: enough to tell a cut from a continuous shot.</summary>
    public const int FrameWidth = 64;

    public const int FrameHeight = 36;

    private const int FrameBytes = FrameWidth * FrameHeight;

    /// <summary>The best score must be under this share of the next best to be called the film.</summary>
    public const double ClearWinner = 0.7;

    private readonly RipparrOptions _options = options.Value;

    /// <summary>The mean picture change (0-255) between two grey frames of the same size.</summary>
    public static double Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var length = Math.Min(a.Length, b.Length);
        if (length == 0)
        {
            return 255;
        }

        long total = 0;
        for (var i = 0; i < length; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return total / (double)length;
    }

    /// <summary>
    /// A playlist's join score: the mean change from the last picture of each clip to the first
    /// of the next. Null when it has no joins or a clip's pictures are missing.
    /// </summary>
    public static double? Score(IReadOnlyList<string> clips, IReadOnlyDictionary<string, (byte[] First, byte[] Last)> frames)
    {
        if (clips.Count < 2)
        {
            return null;
        }

        var total = 0.0;
        for (var i = 1; i < clips.Count; i++)
        {
            if (!frames.TryGetValue(clips[i - 1], out var before) || !frames.TryGetValue(clips[i], out var after))
            {
                return null;
            }

            total += Difference(before.Last, after.First);
        }

        return total / (clips.Count - 1);
    }

    /// <summary>The playlist with the smallest score, when it is under <see cref="ClearWinner"/> of the next one.</summary>
    public static string? Winner(IReadOnlyDictionary<string, double> scores)
    {
        var ranked = scores.OrderBy(s => s.Value).ToList();
        if (ranked.Count == 0)
        {
            return null;
        }

        if (ranked.Count == 1)
        {
            return ranked[0].Key;
        }

        return ranked[0].Value < ranked[1].Value * ClearWinner || ranked[0].Value < 1 && ranked[1].Value >= 3 ? ranked[0].Key : null;
    }

    /// <summary>
    /// Scores the joins of every lookalike playlist (and the main one), writes the scores on them
    /// and picks the winner on the analysis. Returns what to tell the user.
    /// </summary>
    public async Task<string> CheckJoinsAsync(DiscAnalysis analysis, Action<string> status, CancellationToken cancellationToken)
    {
        var playlists = analysis.Playlists.Where(p => p.Lookalike || p.MainFeature).ToList();
        if (playlists.Count < 2)
        {
            return "Only one playlist has the film's length: nothing to compare.";
        }

        var clips = playlists.SelectMany(p => p.Mpls.Clips).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = await OnDiscAsync(analysis.Source, async disc =>
        {
            var frames = new Dictionary<string, (byte[] First, byte[] Last)>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var clip in clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status($"Reading the first and last pictures of clip {++n} of {clips.Count}...");
                var path = DiscAnalyzer.ClipPath(disc, clip);
                if (path == null)
                {
                    continue;
                }

                var first = await FrameAsync(path, null, cancellationToken);
                // A seek that lands past the last key frame decodes nothing: go back further.
                var last = await FrameAsync(path, 1.5, cancellationToken) ?? await FrameAsync(path, 6, cancellationToken);
                if (first != null && last != null)
                {
                    frames[clip] = (first, last);
                }
            }

            return frames;
        });
        if (result == null)
        {
            return "Checking the joins of an ISO needs the mount service.";
        }

        var scores = new Dictionary<string, double>();
        foreach (var playlist in playlists)
        {
            playlist.JoinScore = Score(playlist.Mpls.Clips, result);
            if (playlist.JoinScore is { } score)
            {
                scores[playlist.Name] = score;
            }
        }

        if (scores.Count == 0)
        {
            return "ffmpeg could not read the clips' pictures.";
        }

        analysis.JoinPick = Winner(scores);
        analysis.Classify(TimeSpan.FromSeconds(_options.MinPlaylistSeconds));
        return analysis.JoinPick != null
            ? $"Playlist {analysis.JoinPick} joins its clips most smoothly: it is now the main feature."
            : "No playlist stands clearly apart on its joins; compare the scores, or ask MakeMKV.";
    }

    /// <summary>Asks makemkvcon which title is the main feature and picks it on the analysis.</summary>
    public async Task<string> AskMakeMkvAsync(DiscAnalysis analysis, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.MakeMkvHome);
        var spec = new ProcessSpec(_options.Tools.MakeMkvCon,
            ["-r", "--noscan", "--cache=1", "info", MakeMkv.SourceArgument(analysis.Source)])
        {
            Environment = new Dictionary<string, string> { ["HOME"] = Path.GetFullPath(_options.MakeMkvHome) }
        };
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(spec, TimeSpan.FromMinutes(10), null, cancellationToken);
        }
        catch (Win32Exception)
        {
            return analysis.MakeMkvNote = "makemkvcon is not installed.";
        }

        var pick = MainFeatureOf(result.Output);
        analysis.MakeMkvPick = pick;
        var noJava = JavaFound() ? "" : " No Java runtime is installed, so MakeMKV could not run the disc's BD-J code, which is what finds the real playlist on fake-playlist discs.";
        if (pick == null)
        {
            var reason = MakeMkv.KeyMessages(result.Output).FirstOrDefault()
                         ?? (result.Success ? "it read the disc but named no main feature" : $"it stopped with code {result.ExitCode}");
            logger.LogInformation("MakeMKV named no main feature for {Path}: {Reason}", analysis.Source.Path, reason);
            return analysis.MakeMkvNote = $"MakeMKV did not name a main feature ({reason}).{noJava}";
        }

        analysis.Classify(TimeSpan.FromSeconds(_options.MinPlaylistSeconds));
        var found = analysis.Find(pick);
        var note = found == null
            ? $"MakeMKV calls playlist {pick} the main feature, but the disc has no {pick}.mpls file: the disc's menu builds it at play time, so Ripparr cannot demux it yet."
            : found.MainFeature || analysis.Find(found.DuplicateOf ?? "") is { MainFeature: true }
                ? $"MakeMKV calls playlist {pick} the main feature: it is now the main feature."
                : $"MakeMKV calls playlist {pick} the main feature, but Ripparr could not take it.";
        return analysis.MakeMkvNote = note + noJava;
    }

    /// <summary>True when a java command is on the PATH, where makemkvcon looks for one.</summary>
    public static bool JavaFound() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(folder => folder.Length > 0 && File.Exists(Path.Combine(folder, "java")));

    /// <summary>
    /// The playlist ("00800") of the title makemkvcon marks as the main feature. In robot output a
    /// title's playlist is its source file name (TINFO:n,16,0,"00800.mpls") and the mark is a
    /// TINFO value naming "MainFeature" (FPL_MainFeature).
    /// </summary>
    public static string? MainFeatureOf(string output)
    {
        var sources = new Dictionary<string, string>();
        var marked = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            if (MakeMkv.ParseRobot(raw.Trim()) is not { Type: "TINFO", Values.Count: >= 4 } line)
            {
                continue;
            }

            var title = line.Values[0];
            var value = line.Values[3];
            if (line.Values[1] == "16" && value.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase))
            {
                sources[title] = Path.GetFileNameWithoutExtension(value);
            }

            if (value.Contains("MainFeature", StringComparison.OrdinalIgnoreCase))
            {
                marked.Add(title);
            }
        }

        return marked.Select(t => sources.GetValueOrDefault(t)).FirstOrDefault(p => p != null);
    }

    /// <summary>One small grey picture from the start or the end of a clip, or null.</summary>
    /// <param name="fromEnd">Seconds before the end to start reading (the last picture is kept), or null for the first picture.</param>
    private async Task<byte[]?> FrameAsync(string clip, double? fromEnd, CancellationToken cancellationToken)
    {
        List<string> arguments = ["-v", "error", "-nostdin"];
        if (fromEnd is { } seconds)
        {
            arguments.AddRange(["-sseof", string.Create(CultureInfo.InvariantCulture, $"-{seconds}")]);
        }

        arguments.AddRange(["-i", clip, "-map", "0:v:0", "-an", "-sn",
            "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={FrameWidth}:{FrameHeight},format=gray")]);
        if (fromEnd == null)
        {
            arguments.AddRange(["-frames:v", "1"]);
        }

        arguments.AddRange(["-f", "rawvideo", "pipe:1"]);
        try
        {
            var bytes = await ProcessRunner.RunBytesAsync(_options.Tools.FFmpeg, arguments, TimeSpan.FromMinutes(2), cancellationToken);
            // The last picture of the tail is the clip's last one.
            return bytes is { Length: >= FrameBytes } ? bytes[^FrameBytes..] : null;
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
        {
            logger.LogDebug(e, "No picture from {Clip}", clip);
            return null;
        }
    }

    private async Task<T?> OnDiscAsync<T>(DiscSource source, Func<string, Task<T>> work) where T : class
    {
        if (source.Kind != SourceKind.Iso)
        {
            return await work(SourceFinder.BdmvParent(source.Path) ?? source.Path);
        }

        return await IsoMount.BorrowAsync(_options.IsoMount, source.Path,
            store.List(50, [JobStatus.Running]).FirstOrDefault(j => j.SourcePath == source.Path && j.MountedAt != null)?.MountedAt,
            () => store.List(50, [JobStatus.Running]).Any(j => j.SourcePath == source.Path),
            work);
    }
}
