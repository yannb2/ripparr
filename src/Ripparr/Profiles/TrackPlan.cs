using System.Text.Json.Serialization;
using Ripparr.Disc;
using Ripparr.Media;

namespace Ripparr.Profiles;

/// <summary>One stream of the playlist and what the job does with it. Editable on the job form.</summary>
public sealed class PlannedTrack
{
    /// <summary>Position in the playlist's stream list: ties a demuxed file back to this track.</summary>
    public int StreamIndex { get; set; }

    public StreamKind Kind { get; set; }
    public int Pid { get; set; }

    /// <summary>What the disc says (TrueHD, PGS...), before the stream is read.</summary>
    public string Codec { get; set; } = "";

    /// <summary>"2.0", "Multi"..., audio only.</summary>
    public string? Layout { get; set; }

    /// <summary>Resolution and frame rate, or sample rate, as the playlist gives them.</summary>
    public string? Details { get; set; }

    /// <summary>The disc's language code.</summary>
    public string? DiscLanguage { get; set; }

    /// <summary>The language written to the MKV.</summary>
    public string Language { get; set; } = "und";

    public bool Keep { get; set; }
    public bool Default { get; set; }
    public bool Forced { get; set; }
    public bool Commentary { get; set; }

    /// <summary>Subtitles for the deaf and hard of hearing (SDH): ticked on the job, or found by the OCR.</summary>
    public bool HearingImpaired { get; set; }

    /// <summary>Audio description for the visually impaired (AD).</summary>
    public bool VisualImpaired { get; set; }

    /// <summary>A name typed on the form; empty = from the profile's template.</summary>
    public string? Name { get; set; }

    /// <summary>The Dolby Vision enhancement layer: never its own track, merged into the video.</summary>
    public bool DolbyVisionLayer { get; set; }

    [JsonIgnore]
    public bool Lossless => Codec is "TrueHD" or "DTS-HD MA" or "PCM" or "FLAC";
}

public sealed class TrackPlan
{
    public List<PlannedTrack> Tracks { get; set; } = [];

    /// <summary>The tracks written to the MKV, in order.</summary>
    [JsonIgnore]
    public IEnumerable<PlannedTrack> Kept => Tracks.Where(t => t.Keep && !t.DolbyVisionLayer);

    [JsonIgnore]
    public bool MergeDolbyVisionLayer => Tracks.Any(t => t.DolbyVisionLayer && t.Keep);

    public void Move(PlannedTrack track, int delta)
    {
        var index = Tracks.IndexOf(track);
        var target = index + delta;
        // Only within the same kind: video, then audio, then subtitles.
        if (index < 0 || target < 0 || target >= Tracks.Count || Tracks[target].Kind != track.Kind)
        {
            return;
        }

        (Tracks[index], Tracks[target]) = (Tracks[target], Tracks[index]);
    }

    /// <summary>Makes one Default per kind at most: the first kept video, the given audio, forced subtitles only.</summary>
    public void SetDefault(PlannedTrack track)
    {
        foreach (var other in Tracks.Where(t => t.Kind == track.Kind && t != track))
        {
            other.Default = false;
        }

        track.Default = true;
    }
}

/// <summary>Turns a playlist's streams and a profile into a first plan.</summary>
public static class TrackPlanner
{
    public static TrackPlan Build(Mpls playlist, RipProfile profile, string? originalLanguage)
    {
        var original = string.IsNullOrWhiteSpace(originalLanguage) ? null : originalLanguage.Trim().ToLowerInvariant();
        var audioOrder = Resolve(profile.AudioLanguages, original);
        var subtitleOrder = Resolve(profile.SubtitleLanguages, original);

        var all = playlist.Streams.Select((s, i) => new PlannedTrack
        {
            StreamIndex = i,
            Kind = s.Kind,
            Pid = s.Pid,
            Codec = s.CodecName,
            Layout = s.Kind == StreamKind.Audio ? StreamCoding.AudioLayout(s.Format) : null,
            Details = StreamCoding.Details(s),
            DiscLanguage = s.Language,
            Language = s.Language ?? "und",
            DolbyVisionLayer = s.DolbyVisionEnhancementLayer
        }).Where(t => t.Kind != StreamKind.Other).ToList();

        // Video: the first one, plus the Dolby Vision layer when the profile keeps it.
        var videos = all.Where(t => t.Kind == StreamKind.Video).ToList();
        var mainVideo = videos.FirstOrDefault(t => !t.DolbyVisionLayer);
        foreach (var video in videos)
        {
            video.Keep = video == mainVideo || video.DolbyVisionLayer; // the layer is always merged
            video.Language = profile.VideoLanguage switch
            {
                "original" => original ?? "und",
                "" => "und",
                var code => code
            };
        }

        if (mainVideo != null)
        {
            mainVideo.Default = true;
        }

        // Audio: wanted languages first, in the profile's order, then the disc's order.
        var audio = all.Where(t => t.Kind == StreamKind.Audio).ToList();
        MarkCommentaries(audio);
        foreach (var track in audio)
        {
            var wanted = Rank(audioOrder, track.Language) < int.MaxValue || profile.KeepOtherAudioLanguages;
            track.Keep = wanted && (!track.Commentary || profile.KeepCommentary);
        }

        if (profile.DropLossyWhenLossless)
        {
            foreach (var track in audio.Where(t => t.Keep && !t.Lossless && !t.Commentary))
            {
                if (audio.Any(o => o.Keep && o.Lossless && !o.Commentary && Languages.Same(o.Language, track.Language)))
                {
                    track.Keep = false;
                }
            }
        }

        audio = audio
            .OrderBy(t => t.Commentary)
            .ThenBy(t => Rank(audioOrder, t.Language))
            .ThenBy(t => t.StreamIndex)
            .ToList();
        var firstAudio = audio.FirstOrDefault(t => t.Keep && !t.Commentary);
        if (firstAudio != null)
        {
            firstAudio.Default = true;
        }

        // Subtitles: never Default unless forced (the form can tick Forced).
        var subtitles = all.Where(t => t.Kind == StreamKind.Subtitle).ToList();
        foreach (var track in subtitles)
        {
            track.Keep = Rank(subtitleOrder, track.Language) < int.MaxValue || profile.KeepOtherSubtitleLanguages;
        }

        subtitles = subtitles.OrderBy(t => Rank(subtitleOrder, t.Language)).ThenBy(t => t.StreamIndex).ToList();

        return new TrackPlan { Tracks = [..videos, ..audio, ..subtitles] };
    }

    /// <summary>
    /// A stereo lossy track in a language that already has a multichannel one, listed after
    /// it, is almost always a commentary. Only a guess: the form shows it and it can be unticked.
    /// </summary>
    internal static void MarkCommentaries(List<PlannedTrack> audio)
    {
        for (var i = 0; i < audio.Count; i++)
        {
            var track = audio[i];
            if (track.Lossless || track.Layout is not ("2.0" or "1.0"))
            {
                continue;
            }

            track.Commentary = audio.Take(i).Any(o => Languages.Same(o.Language, track.Language) && o.Layout is "Multi" or "2.0 + Multi");
        }
    }

    private static List<string> Resolve(IEnumerable<string> languages, string? original)
    {
        var list = new List<string>();
        foreach (var language in languages)
        {
            var code = language.Trim().ToLowerInvariant() == "original" ? original : language.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(code) && !list.Any(l => Languages.Same(l, code)))
            {
                list.Add(code);
            }
        }

        return list;
    }

    private static int Rank(List<string> order, string? language)
    {
        var index = order.FindIndex(l => Languages.Same(l, language));
        return index < 0 ? int.MaxValue : index;
    }
}
