using System.Globalization;

namespace Ripparr.Profiles;

/// <summary>
/// The DGDemux switches a profile turns on, from DGDemux.txt (1.0.0.85) and DGDemuxGUI's
/// checkboxes. Ripparr merges the Dolby Vision layer itself with dovi_tool, reads the chapters
/// from the playlist and runs one playlist per job, so -mergedv, -sslc, -ep, -collapse, -z and
/// -extras are not offered.
/// </summary>
public sealed class DgDemuxSettings
{
    /// <summary>-demux: only the streams the job keeps (and the Dolby Vision layer), not the whole playlist.</summary>
    public bool DemuxOnlyKept { get; set; } = true;

    /// <summary>-ang n: the angle to demux, from 1. 0 = DGDemux's default (the first).</summary>
    public int Angle { get; set; }

    /// <summary>
    /// -thd n: 0 = THD, the embedded AC3 and a THD+AC3 file; 1 = THD and the embedded AC3;
    /// 2 = THD only. Null = from the profile: 1 when the AC-3 core is kept, otherwise 2.
    /// </summary>
    public int? ThdMode { get; set; }

    /// <summary>-sf: skip the first clip (a short logo clip without audio can hide the audio).</summary>
    public bool SkipFirstClip { get; set; }

    /// <summary>-sl: skip the last clip.</summary>
    public bool SkipLastClip { get; set; }

    /// <summary>-strictpl: honour each clip's IN and OUT times (drops extra content around them, e.g. KINO LORBER discs).</summary>
    public bool StrictPlaylist { get; set; }

    /// <summary>-nopre: no disk space preallocation (DGDemux: not implemented for linux yet).</summary>
    public bool NoPreallocation { get; set; }

    /// <summary>-nogaps: no audio gap processing (DGDemux: research and debugging).</summary>
    public bool NoGaps { get; set; }
}

/// <summary>Builds DGDemux's arguments from the global template, the profile's switches and its extra arguments.</summary>
public static class DgDemuxCommand
{
    /// <summary>The switches, for the profile page: flag, label, getter and setter.</summary>
    public static readonly IReadOnlyList<(string Flag, string Label, Func<DgDemuxSettings, bool> Get, Action<DgDemuxSettings, bool> Set)> Switches =
    [
        ("-sf", "Skip the first clip (a short logo clip without audio can stop the audio being found)", s => s.SkipFirstClip, (s, v) => s.SkipFirstClip = v),
        ("-sl", "Skip the last clip", s => s.SkipLastClip, (s, v) => s.SkipLastClip = v),
        ("-strictpl", "Strict playlist: honour each clip's IN and OUT times and drop the content outside them", s => s.StrictPlaylist, (s, v) => s.StrictPlaylist = v),
        ("-nopre", "No disk space preallocation (DGDemux: not implemented for linux yet)", s => s.NoPreallocation, (s, v) => s.NoPreallocation = v),
        ("-nogaps", "No audio gap processing (DGDemux: research and debugging only)", s => s.NoGaps, (s, v) => s.NoGaps = v)
    ];

    /// <summary>The -thd mode a job uses: the profile's, or 1 with the AC-3 core kept and 2 without.</summary>
    public static int ThdMode(RipProfile profile) => profile.DgDemux.ThdMode ?? (profile.KeepTrueHdCore ? 1 : 2);


    /// <summary>A profile's own full template (with {input}) replaces everything else, as before 0.2.1.</summary>
    public static bool IsFullTemplate(string? arguments) =>
        arguments != null && arguments.Contains("{input}", StringComparison.Ordinal);

    /// <summary>DGDemux's -demux takes decimal PIDs: 0x1011 is 4113, 0x1100 is 4352.</summary>
    public static string Pid(int pid) => pid.ToString(CultureInfo.InvariantCulture);

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

        arguments.AddRange(["-thd", ThdMode(profile).ToString(CultureInfo.InvariantCulture)]);
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
