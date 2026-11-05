using Ripparr.Profiles;

namespace Ripparr.Disc;

/// <summary>
/// Puts a subtitle read on an episode's or extra's plan, by the film form's rules: caption counts on
/// the tracks, and the SDH (when the profile detects SDH) and commentary flags the read found, once
/// per read; a flag changed by hand since the app last set it stays as it is. After a full read of
/// every caption the guesses are firm and set the flags both ways.
/// </summary>
public static class SubtitleFlags
{
    public static void Apply(TrackPlan plan, PlaylistTracksState state, SubtitleScan scan, bool detectSdh)
    {
        if (scan is not { Running: false, Error: null })
        {
            return;
        }

        if (state.Plan != plan)
        {
            Reset(plan, state);
        }

        var flag = state.FlaggedVersion != scan.GuessVersion;
        var both = flag && state.FlaggedVersion >= 0 && scan.FullText;
        state.FlaggedVersion = scan.GuessVersion;
        foreach (var track in plan.Tracks.Where(t => t.Kind == StreamKind.Subtitle))
        {
            var auto = state.Auto.GetValueOrDefault(track.Pid, (Sdh: track.HearingImpaired, Commentary: track.Commentary));
            var sdhByHand = track.HearingImpaired != auto.Sdh;
            var commentaryByHand = track.Commentary != auto.Commentary;
            if (scan.Tracks.TryGetValue(track.Pid, out var read))
            {
                track.Details = scan.Quick
                    ? $"{read.Captions:N0} elements in the sample"
                    : $"{read.Captions:N0} elements" + (read.Forced > 0 ? $", {read.Forced:N0} forced" : "");
                if (flag && detectSdh && (read.Sdh || both) && !sdhByHand)
                {
                    track.HearingImpaired = read.Sdh;
                }

                if (flag && (read.Commentary || both) && !commentaryByHand)
                {
                    track.Commentary = read.Commentary;
                }
            }

            state.Auto[track.Pid] = (sdhByHand ? auto.Sdh : track.HearingImpaired, commentaryByHand ? auto.Commentary : track.Commentary);
        }
    }

    /// <summary>A new plan (first open, or Reset): the flags as the profile set them are "the app's".</summary>
    public static void Reset(TrackPlan plan, PlaylistTracksState state)
    {
        state.Plan = plan;
        state.FlaggedVersion = -1;
        state.Auto = plan.Tracks.Where(t => t.Kind == StreamKind.Subtitle).ToDictionary(t => t.Pid, t => (t.HearingImpaired, t.Commentary));
    }
}
