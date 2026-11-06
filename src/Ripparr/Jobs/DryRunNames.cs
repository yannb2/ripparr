using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Jobs;

/// <summary>One track of the file a dry run would write, with the name the profile's templates give it.</summary>
/// <param name="Guessed">The name comes from the playlist, not from the streams: channels, Atmos and DTS:X can still change.</param>
/// <param name="Core">The AC-3 core the job keeps next to a TrueHD track.</param>
public sealed record DryRunTrack(PlannedTrack Track, string Name, bool Guessed, bool Core = false);

/// <summary>
/// The track names a dry run shows. They come from the same templates as a real job. The facts come
/// from the Disc page's quick read of the streams (mediainfo) when it ran, and otherwise from what the
/// playlist says. Only the Dolby Vision layer (FEL or MEL) waits for dovi_tool during the job.
/// </summary>
public static class DryRunNames
{
    public static List<DryRunTrack> For(Job job, PlaylistInfo playlist)
    {
        var tracks = new List<DryRunTrack>();
        var mergeDv = job.Plan.MergeDolbyVisionLayer;
        foreach (var track in job.Plan.Kept)
        {
            var stream = playlist.Mpls.Streams.ElementAtOrDefault(track.StreamIndex);
            var guessed = track.Probed == null;
            var facts = track.Probed ?? Guess(track, stream, playlist);
            string? dvProfile = null;
            if (track.Kind == StreamKind.Video && mergeDv)
            {
                // The job merges the enhancement layer into the video; dovi_tool then reads its profile (7 on UHD discs).
                facts = MuxBuilder.WithEnhancementLayer(facts);
                dvProfile = "7";
            }

            tracks.Add(new DryRunTrack(track, MuxBuilder.TrackName(track, facts, job.Profile, job.OriginalLanguage, dvProfile), guessed));

            if (job.Profile.KeepTrueHdCore && track.Kind == StreamKind.Audio && IsTrueHd(track, facts))
            {
                // Same track as MuxBuilder.Outputs adds for the AC-3 core; a core has at most 5.1.
                var core = new PlannedTrack
                {
                    StreamIndex = track.StreamIndex, Kind = StreamKind.Audio, Codec = "AC3", Language = track.Language,
                    Keep = true, Commentary = track.Commentary, VisualImpaired = track.VisualImpaired, Pid = track.Pid
                };
                var coreFacts = new StreamFacts { Kind = StreamKind.Audio, Format = "AC-3", Channels = Math.Min(facts.Channels, 6) };
                tracks.Add(new DryRunTrack(core, MuxBuilder.TrackName(core, coreFacts, job.Profile, job.OriginalLanguage), true, Core: true));
            }
        }

        return tracks;
    }

    /// <summary>One line of the dry run's track list: what the track is, then its name.</summary>
    public static string Line(DryRunTrack dry)
    {
        var track = dry.Track;
        var what = $"{track.Kind} {track.Language} {track.Codec}{(track.Default ? " default" : "")}{(track.Forced ? " forced" : "")}" +
                   $"{(track.Commentary ? " commentary" : "")}{(track.HearingImpaired ? " SDH" : "")}{(track.VisualImpaired ? " AD" : "")}";
        return $"  {what}{(dry.Core ? " (TrueHD core)" : "")} → \"{dry.Name}\"{(dry.Guessed ? " ~" : "")}";
    }

    /// <summary>Notes under the list: which names are guesses, and what only the job can read.</summary>
    public static IEnumerable<string> Notes(Job job, IReadOnlyList<DryRunTrack> tracks)
    {
        if (tracks.Any(t => t.Guessed))
        {
            yield return "  ~ from the playlist, not the streams: channels, Atmos and DTS:X (and an AC-3 core's channels) are read during the job.";
        }

        var dvTemplate = string.IsNullOrWhiteSpace(job.Profile.DolbyVisionTemplate) ? TrackNaming.DefaultDolbyVision : job.Profile.DolbyVisionTemplate;
        if (job.Plan.MergeDolbyVisionLayer && dvTemplate.Contains("{dvlayer}", StringComparison.OrdinalIgnoreCase))
        {
            yield return "  The Dolby Vision layer (FEL or MEL) in the video's name is read by dovi_tool during the job.";
        }
    }

    private static bool IsTrueHd(PlannedTrack track, StreamFacts facts) =>
        track.Codec == "TrueHD" || facts.AudioCodec.StartsWith("TrueHD", StringComparison.Ordinal);

    /// <summary>What mediainfo would say, from the playlist alone (and the DVD's IFO for its audio channels).</summary>
    public static StreamFacts Guess(PlannedTrack track, PlaylistStream? stream, PlaylistInfo playlist)
    {
        switch (track.Kind)
        {
            case StreamKind.Video:
                var (width, height) = (playlist.Resolution ?? (stream == null ? null : StreamCoding.Resolution(stream.Format))) switch
                {
                    "2160p" => (3840, 2160),
                    "1080p" or "1080i" => (1920, 1080),
                    "720p" => (1280, 720),
                    "576p" or "576i" => (720, 576),
                    "480p" or "480i" => (720, 480),
                    _ => (0, 0)
                };
                var hdr10 = stream?.DynamicRange is 1 or 2;
                return new StreamFacts
                {
                    Kind = StreamKind.Video,
                    Format = (stream?.CodecName ?? track.Codec) switch
                    {
                        "MPEG2" or "MPEG1" => "MPEG Video",
                        var codec => codec
                    },
                    Width = width,
                    Height = height,
                    HdrFormat = stream?.DynamicRange == 2 ? "Dolby Vision" : stream?.Hdr10Plus == true ? "SMPTE ST 2094 App 4" : hdr10 ? "SMPTE ST 2086" : null,
                    HdrCompatibility = hdr10 ? stream?.Hdr10Plus == true ? "HDR10+ Profile A / HDR10" : "HDR10" : null
                };
            case StreamKind.Audio:
                // No Format: the name then takes the disc's codec word (TrueHD, DTS-HD MA, AC3...).
                return new StreamFacts { Kind = StreamKind.Audio, Channels = Channels(track, stream, playlist) };
            default:
                return new StreamFacts { Kind = track.Kind };
        }
    }

    private static int Channels(PlannedTrack track, PlaylistStream? stream, PlaylistInfo playlist)
    {
        if (playlist.Dvd is { } dvd && stream != null && dvd.Audio.FirstOrDefault(a => a.StreamId == stream.Pid) is { } audio)
        {
            return audio.Channels;
        }

        return track.Layout switch
        {
            "1.0" => 1,
            "2.0" => 2,
            _ => 0 // "Multi": the playlist doesn't say how many
        };
    }
}
