using System.Globalization;

namespace Ripparr.Profiles;

/// <summary>
/// The DGDemux switches a profile turns on, from the DGDemux manual (DGDemux.txt) and the
/// options DGDemuxGUI offers. Run <c>dgdemux -h</c> (the button on the profile page) to see
/// what the installed version takes.
/// </summary>
public sealed class DgDemuxSettings
{
    /// <summary>-demux: only the streams the job keeps (and the Dolby Vision layer), not the whole playlist.</summary>
    public bool DemuxOnlyKept { get; set; } = true;

    /// <summary>-ang n: the angle to demux, from 1. 0 = DGDemux's default (the first).</summary>
    public int Angle { get; set; }

    /// <summary>-sf: skip the first clip (a short logo clip can upset the audio detection).</summary>
    public bool SkipFirstClip { get; set; }

    /// <summary>-sl: skip the last clip.</summary>
    public bool SkipLastClip { get; set; }

    /// <summary>-sp: strict playlist mode, nothing before the first clip's IN time or after the last clip's OUT time.</summary>
    public bool StrictPlaylist { get; set; }

    /// <summary>-ch: cut the audio that runs past each clip's OUT time.</summary>
    public bool ClipAudio { get; set; }

    /// <summary>-nopre: do not reserve the disk space first (helps when space is short).</summary>
    public bool NoPreallocation { get; set; }

    /// <summary>-nogaps: no audio gap processing (DGDemux says: for research and debugging).</summary>
    public bool NoGaps { get; set; }

    /// <summary>-fo: forced subtitles only.</summary>
    public bool ForcedSubtitlesOnly { get; set; }
}

/// <summary>Builds DGDemux's arguments from the global template, the profile's switches and its extra arguments.</summary>
public static class DgDemuxCommand
{
    /// <summary>The switches, for the profile page: flag, label, getter and setter.</summary>
    public static readonly IReadOnlyList<(string Flag, string Label, Func<DgDemuxSettings, bool> Get, Action<DgDemuxSettings, bool> Set)> Switches =
    [
        ("-sf", "Skip the first clip (a short logo clip can upset the audio detection)", s => s.SkipFirstClip, (s, v) => s.SkipFirstClip = v),
        ("-sl", "Skip the last clip", s => s.SkipLastClip, (s, v) => s.SkipLastClip = v),
        ("-sp", "Strict playlist: drop what is outside the first clip's IN time and the last clip's OUT time", s => s.StrictPlaylist, (s, v) => s.StrictPlaylist = v),
        ("-ch", "Cut audio that runs past each clip's OUT time", s => s.ClipAudio, (s, v) => s.ClipAudio = v),
        ("-nopre", "Do not reserve the disk space first", s => s.NoPreallocation, (s, v) => s.NoPreallocation = v),
        ("-nogaps", "No audio gap processing (DGDemux: research and debugging only)", s => s.NoGaps, (s, v) => s.NoGaps = v),
        ("-fo", "Forced subtitles only (other subtitle tracks are not written)", s => s.ForcedSubtitlesOnly, (s, v) => s.ForcedSubtitlesOnly = v)
    ];

    /// <summary>A profile's own full template (with {input}) replaces everything else, as before 0.2.1.</summary>
    public static bool IsFullTemplate(string? arguments) =>
        arguments != null && arguments.Contains("{input}", StringComparison.Ordinal);

    /// <summary>DGDemux's PID words: 1011, 1100, 1A00.</summary>
    public static string Pid(int pid) => pid.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>The PIDs a job asks for: the kept tracks and, when merged, the Dolby Vision layer.</summary>
    public static List<int> Pids(TrackPlan plan) =>
        plan.Tracks.Where(t => t.Keep && t.Pid > 0).Select(t => t.Pid).Distinct().ToList();

    public static List<string> Arguments(RipProfile profile, string globalTemplate, string input, string output, IReadOnlyCollection<int>? pids)
    {
        if (IsFullTemplate(profile.DgDemuxArguments))
        {
            return Expand(profile.DgDemuxArguments, input, output);
        }

        var arguments = Expand(globalTemplate, input, output);
        var settings = profile.DgDemux;
        if (settings.Angle > 1)
        {
            arguments.AddRange(["-ang", settings.Angle.ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(Switches.Where(s => s.Get(settings)).Select(s => s.Flag));
        if (settings.DemuxOnlyKept && pids is { Count: > 0 })
        {
            arguments.AddRange(["-demux", string.Join(',', pids.Select(Pid))]);
        }

        arguments.AddRange(Split(profile.DgDemuxArguments));
        return arguments;
    }

    /// <summary>The PIDs DGDemux will write, or null for all of them.</summary>
    public static IReadOnlySet<int>? DemuxedPids(RipProfile profile, TrackPlan plan)
    {
        if (IsFullTemplate(profile.DgDemuxArguments) || !profile.DgDemux.DemuxOnlyKept)
        {
            return null;
        }

        var pids = Pids(plan);
        return pids.Count > 0 ? pids.ToHashSet() : null;
    }

    public static List<string> Expand(string template, string input, string output) =>
        Split(template).Select(a => a.Replace("{input}", input).Replace("{output}", output)).ToList();

    private static List<string> Split(string? text) =>
        (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
}
