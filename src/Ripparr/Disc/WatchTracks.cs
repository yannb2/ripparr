using System.Globalization;
using Ripparr.Profiles;

namespace Ripparr.Disc;

/// <summary>
/// The audio track a Watch preview plays and the subtitle track it burns in, by PID: the ones the
/// job picked (its Default audio, its first kept subtitle), not the disc's first audio and no
/// subtitles. Null plays the first audio track / shows no subtitles, as before.
/// </summary>
public sealed record WatchTracks(int? Audio, int? Subtitle)
{
    public static readonly WatchTracks None = new(null, null);

    /// <summary>
    /// What the job plays: its Default audio (else its first kept audio track), and its Default
    /// subtitle (forced) else its first kept one, in the job's order.
    /// </summary>
    public static WatchTracks From(TrackPlan? plan)
    {
        if (plan == null)
        {
            return None;
        }

        var audio = plan.Kept.Where(t => t.Kind == StreamKind.Audio).ToList();
        var subtitles = plan.Kept.Where(t => t.Kind == StreamKind.Subtitle).ToList();
        return new WatchTracks(
            (audio.FirstOrDefault(t => t.Default) ?? audio.FirstOrDefault())?.Pid,
            (subtitles.FirstOrDefault(t => t.Default) ?? subtitles.FirstOrDefault())?.Pid);
    }

    /// <summary>
    /// Keeps only the PIDs the playlist has, of the right kind (the URL can say anything). A
    /// subtitle with no audio plays it over the playlist's first audio track.
    /// </summary>
    public static WatchTracks Check(Mpls playlist, int? audio, int? subtitle)
    {
        var checkedAudio = audio is { } a && playlist.Streams.Any(s => s.Kind == StreamKind.Audio && s.Pid == a) ? a : (int?)null;
        var checkedSubtitle = subtitle is { } p && playlist.Streams.Any(s => s.Kind == StreamKind.Subtitle && s.Pid == p && s.Coding == StreamCoding.Pgs)
            ? p
            : (int?)null;
        if (checkedAudio == null && checkedSubtitle == null)
        {
            return None;
        }

        checkedAudio ??= playlist.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio)?.Pid;
        return new WatchTracks(checkedAudio, checkedSubtitle);
    }

    /// <summary>The main video's PID: what the live player's concat list asks for first.</summary>
    public static int? VideoPid(Mpls playlist) =>
        playlist.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video && !s.DolbyVisionEnhancementLayer)?.Pid;

    /// <summary>Part of a cut preview's file name, so each choice has its own.</summary>
    public string FileSuffix =>
        (Audio is { } a ? $"-a{a.ToString(CultureInfo.InvariantCulture)}" : "") +
        (Subtitle is { } s ? $"-s{s.ToString(CultureInfo.InvariantCulture)}" : "");

    /// <summary>The URL's query part, starting with &amp; (empty when nothing is chosen).</summary>
    public string Query =>
        (Audio is { } a ? $"&audio={a.ToString(CultureInfo.InvariantCulture)}" : "") +
        (Subtitle is { } s ? $"&subtitle={s.ToString(CultureInfo.InvariantCulture)}" : "");

    /// <summary>
    /// ffmpeg's video part: scaled to <paramref name="scale"/>, with the subtitle track (a picture,
    /// on the same 16:9 canvas as the video) scaled the same way and laid over it.
    /// </summary>
    public static string[] VideoArguments(string video, string? subtitle, string scale) => subtitle == null
        ? ["-map", video, "-vf", $"{scale},format=yuv420p"]
        : ["-filter_complex", $"[{video}]{scale}[v];[{subtitle}]{scale}[s];[v][s]overlay=eof_action=pass,format=yuv420p[out]", "-map", "[out]"];
}
