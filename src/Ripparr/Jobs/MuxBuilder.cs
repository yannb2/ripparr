using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Jobs;

/// <summary>A file DGDemux wrote, with what mediainfo read from it.</summary>
public sealed record DemuxedFile(string Path, StreamFacts Facts)
{
    public StreamKind Kind => Facts.Kind;

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>A delay written in the file name ("DELAY -24ms"), applied with --sync.</summary>
    public int? DelayMs => MuxBuilder.Delay(FileName);
}

/// <summary>A track of the finished MKV: the plan's choice, the file it comes from, and its name.</summary>
public sealed record OutputTrack(PlannedTrack Planned, DemuxedFile File, string Name);

public sealed class StreamMapping
{
    /// <summary>Playlist stream index -> demuxed file.</summary>
    public Dictionary<int, DemuxedFile> Files { get; } = new();

    /// <summary>The Dolby Vision enhancement layer, when DGDemux wrote it as its own file.</summary>
    public DemuxedFile? EnhancementLayer { get; set; }

    /// <summary>AC-3 cores DGDemux split from a TrueHD track: TrueHD file -> its core.</summary>
    public Dictionary<DemuxedFile, DemuxedFile> Cores { get; } = new();

    public List<string> Warnings { get; } = [];
}

/// <summary>Everything between the demuxed files and the mkvmerge command line. No tools are run here.</summary>
public static partial class MuxBuilder
{
    private static readonly string[] VideoExtensions = [".hevc", ".h265", ".265", ".avc", ".h264", ".264", ".vc1", ".m2v", ".mpv"];
    private static readonly string[] AudioExtensions = [".thd", ".truehd", ".mlp", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".dtsma", ".pcm", ".wav", ".w64", ".flac", ".lpcm"];
    private static readonly string[] SubtitleExtensions = [".sup", ".pgs"];

    /// <summary>Which files are streams worth reading (not logs or chapter files).</summary>
    public static bool IsStreamFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return VideoExtensions.Contains(extension) || AudioExtensions.Contains(extension) || SubtitleExtensions.Contains(extension);
    }

    public static bool IsSubtitleFile(string path) => SubtitleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Facts for a picture subtitle file DGDemux wrote (.sup), from its own captions: MediaInfo
    /// reports no track at all for a bare .sup. Null when the file holds no PGS segments.
    /// </summary>
    public static StreamFacts? SupFacts(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            var captions = size == 0 ? [] : Subtitles.PgsReader.Read(path);
            if (size > 0 && captions.Count == 0 && !StartsWithPg(path))
            {
                return null;
            }

            return new StreamFacts
            {
                Kind = StreamKind.Subtitle,
                Format = "PGS",
                StreamSize = size,
                ElementCount = captions.Count,
                DurationSeconds = captions.Count > 0 ? captions[^1].End.TotalSeconds : 0
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Unreadable or not PGS after all: left to the "could not read" warning.
            return null;
        }
    }

    private static bool StartsWithPg(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[2];
        return stream.Read(head) == 2 && head[0] == (byte)'P' && head[1] == (byte)'G';
    }

    public static int? Delay(string fileName) =>
        DelayRegex().Match(fileName) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var ms) && ms != 0 ? ms : null;

    /// <summary>
    /// Ties each demuxed file to its playlist stream. A file whose name holds a stream's
    /// PID (1100, 0x1100 or 4352) goes to that stream; the rest go by order within each kind.
    /// With <paramref name="demuxedPids"/> (DGDemux -demux), only those streams were written.
    /// </summary>
    public static StreamMapping Map(Mpls playlist, IReadOnlyList<DemuxedFile> files, IReadOnlySet<int>? demuxedPids = null)
    {
        var mapping = new StreamMapping();
        var ordered = files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();

        // TrueHD cores: an AC-3 file named like a TrueHD file.
        var trueHd = ordered.Where(f => f.Kind == StreamKind.Audio && f.Facts.AudioCodec.StartsWith("TrueHD", StringComparison.Ordinal)).ToList();
        foreach (var file in ordered.Where(f => f.Kind == StreamKind.Audio && f.Facts.AudioCodec == "AC3").ToList())
        {
            var stem = Stem(file.FileName);
            var parent = trueHd.FirstOrDefault(t => Stem(t.FileName) == stem
                                                    || file.FileName.Contains("core", StringComparison.OrdinalIgnoreCase)
                                                    && file.FileName.StartsWith(Stem(t.FileName), StringComparison.OrdinalIgnoreCase));
            if (parent != null)
            {
                mapping.Cores[parent] = file;
                ordered.Remove(file);
            }
        }

        // The Dolby Vision layer: the smaller picture of two video files (1080p next to 2160p).
        var videoFiles = ordered.Where(f => f.Kind == StreamKind.Video).OrderByDescending(f => f.Facts.Width).ToList();
        var hasLayer = playlist.Streams.Any(s => s.DolbyVisionEnhancementLayer && (demuxedPids == null || demuxedPids.Contains(s.Pid)));
        if (videoFiles.Count >= 2 && (hasLayer || videoFiles[1].Facts.DolbyVision))
        {
            mapping.EnhancementLayer = videoFiles[1];
            ordered.Remove(videoFiles[1]);
        }

        foreach (var kind in new[] { StreamKind.Video, StreamKind.Audio, StreamKind.Subtitle })
        {
            var streams = playlist.Streams.Select((s, i) => (Stream: s, Index: i))
                .Where(x => x.Stream.Kind == kind && !x.Stream.DolbyVisionEnhancementLayer
                            && (demuxedPids == null || demuxedPids.Contains(x.Stream.Pid)))
                .ToList();
            var pool = ordered.Where(f => f.Kind == kind).ToList();

            foreach (var (stream, index) in streams)
            {
                var byPid = pool.Where(f => HoldsPid(f.FileName, stream.Pid)).ToList();
                if (byPid.Count == 1)
                {
                    mapping.Files[index] = byPid[0];
                    pool.Remove(byPid[0]);
                }
            }

            foreach (var (_, index) in streams.Where(x => !mapping.Files.ContainsKey(x.Index)))
            {
                if (pool.Count == 0)
                {
                    break;
                }

                mapping.Files[index] = pool[0];
                pool.RemoveAt(0);
            }

            var mapped = streams.Count(x => mapping.Files.ContainsKey(x.Index));
            if (mapped < streams.Count)
            {
                mapping.Warnings.Add($"The playlist lists {streams.Count} {Plural(kind)} streams but DGDemux wrote {mapped}.");
            }

            if (pool.Count > 0)
            {
                mapping.Warnings.Add($"DGDemux wrote {pool.Count} more {Plural(kind)} file(s) than the playlist lists: {string.Join(", ", pool.Select(p => p.FileName))}.");
            }
        }

        return mapping;
    }

    /// <summary>The finished file's tracks, in the plan's order, each with its name.</summary>
    public static List<OutputTrack> Outputs(TrackPlan plan, StreamMapping mapping, RipProfile profile, string? originalLanguage = null)
    {
        var outputs = new List<OutputTrack>();
        foreach (var track in plan.Kept)
        {
            if (!mapping.Files.TryGetValue(track.StreamIndex, out var file))
            {
                continue;
            }

            outputs.Add(new OutputTrack(track, file, TrackName(track, file.Facts, profile, originalLanguage)));
            if (profile.KeepTrueHdCore && mapping.Cores.TryGetValue(file, out var core))
            {
                var coreTrack = new PlannedTrack
                {
                    StreamIndex = track.StreamIndex, Kind = StreamKind.Audio, Codec = "AC3", Language = track.Language,
                    Keep = true, Commentary = track.Commentary, VisualImpaired = track.VisualImpaired, Pid = track.Pid
                };
                outputs.Add(new OutputTrack(coreTrack, core, TrackName(coreTrack, core.Facts, profile, originalLanguage)));
            }
        }

        return outputs;
    }

    /// <summary>
    /// A track's flags. Dub: audio in another language than the film's own (known from Radarr or
    /// TMDB), except commentaries and audio descriptions.
    /// </summary>
    public static TrackFlags Flags(PlannedTrack track, string? originalLanguage) => new(
        HearingImpaired: track.HearingImpaired,
        Forced: track.Forced,
        Commentary: track.Commentary,
        VisualImpaired: track.VisualImpaired,
        Dub: track.Kind == StreamKind.Audio && !track.Commentary && !track.VisualImpaired
             && !string.IsNullOrWhiteSpace(originalLanguage) && originalLanguage != "und"
             && !Languages.Same(track.Language, originalLanguage));

    /// <summary>
    /// The track name: typed on the form, or from the profile's templates. The video's Dolby
    /// Vision profile and layer come from dovi_tool once the layers are merged.
    /// </summary>
    public static string TrackName(PlannedTrack track, StreamFacts facts, RipProfile profile, string? originalLanguage = null,
        string? dvProfile = null, string? dvLayer = null)
    {
        if (!string.IsNullOrWhiteSpace(track.Name))
        {
            return track.Name.Trim();
        }

        var language = Languages.Name(track.Language);
        var flags = Flags(track, originalLanguage);
        return track.Kind switch
        {
            StreamKind.Video => TrackNaming.Video(profile, language, facts.Resolution, facts.VideoCodec, facts.DynamicRange,
                TrackNaming.DolbyVisionProfile(dvProfile ?? facts.DolbyVisionProfile, facts.DynamicRange), dvLayer),
            StreamKind.Audio => TrackNaming.Audio(profile, language,
                string.IsNullOrEmpty(facts.Format) ? track.Codec : facts.AudioCodec, facts.ChannelWords, flags),
            StreamKind.Subtitle => TrackNaming.Subtitle(profile, language, SubtitleCodec(track, facts), flags),
            _ => ""
        };
    }

    /// <summary>PGS for Blu-ray picture subtitles, SRT for the OCR text.</summary>
    public static string SubtitleCodec(PlannedTrack track, StreamFacts facts) =>
        string.Equals(facts.Format, "SRT (OCR)", StringComparison.Ordinal) || track.Codec == "SRT" ? "SRT" : string.IsNullOrEmpty(track.Codec) ? "PGS" : track.Codec;

    /// <summary>The values the file name and title templates use.</summary>
    public static Dictionary<string, string?> NameValues(Job job, IReadOnlyList<OutputTrack> outputs)
    {
        var video = outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Video)?.File.Facts;
        var audio = outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Audio && o.Planned.Default)
                    ?? outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Audio);
        var audioWords = audio == null
            ? null
            : string.Join('-', new[] { audio.File.Facts.AudioCodec, audio.File.Facts.ChannelWords }.Where(s => !string.IsNullOrEmpty(s)));
        var range = video?.DynamicRange;
        return new Dictionary<string, string?>
        {
            ["title"] = job.Title,
            ["year"] = job.Year?.ToString(CultureInfo.InvariantCulture),
            ["edition"] = job.Edition,
            ["resolution"] = video?.Resolution,
            ["videocodec"] = video?.VideoCodec,
            // An SDR film gets [SDR], as the rename-only custom format does in Radarr.
            ["hdr"] = video == null ? null : string.IsNullOrEmpty(range) ? "SDR" : range,
            ["audio"] = audioWords,
            ["source"] = job.Profile.Source
        };
    }

    /// <summary>Chapters as an OGM text file mkvmerge reads: CHAPTER01=00:00:00.000 / CHAPTER01NAME=Chapter 01.</summary>
    public static string ChapterFile(Mpls playlist, ChapterNaming naming)
    {
        var text = new StringBuilder();
        var number = 0;
        foreach (var mark in playlist.Chapters)
        {
            number++;
            var id = number.ToString("00", CultureInfo.InvariantCulture);
            text.Append($"CHAPTER{id}={mark.Start:hh\\:mm\\:ss\\.fff}\n");
            text.Append($"CHAPTER{id}NAME={(naming == ChapterNaming.Numbered ? $"Chapter {id}" : "")}\n");
        }

        return text.ToString();
    }

    /// <summary>MKV global tags (TargetTypeValue 50), the same names tag_release.py writes.</summary>
    public static string TagsXml(Job job, IReadOnlyList<OutputTrack> outputs)
    {
        var video = outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Video)?.File.Facts;
        var tags = new List<(string Name, string? Value)>
        {
            ("TMDB", job.TmdbId is { } tmdb ? $"movie/{tmdb}" : null),
            ("IMDB", job.ImdbId),
            ("RELEASE_NAME", job.SourceName),
            ("SOURCE", job.Profile.Source),
            ("VIDEO_CODEC", video?.VideoCodec),
            ("HDR_FORMAT", video?.DynamicRange is { Length: > 0 } hdr ? hdr : null),
            ("DV_PROFILE", video is { DolbyVision: true } ? video.DolbyVisionProfile : null),
            ("EDITION", string.IsNullOrWhiteSpace(job.Edition) ? null : job.Edition),
            ("ORIGINAL_LANGUAGE", job.OriginalLanguage),
            ("RIPPED_WITH", $"Ripparr {AppInfo.Version}, playlist {job.Playlist}")
        };

        // The job's own tags: placeholders filled like the file name; one with an automatic tag's name replaces it.
        var values = NameValues(job, outputs);
        foreach (var tag in job.Tags)
        {
            var name = GlobalTag.Normalise(tag.Name);
            if (name.Length == 0)
            {
                continue;
            }

            var value = tag.Value.Contains('{') ? Naming.Apply(tag.Value, values) : tag.Value.Trim();
            tags.RemoveAll(t => t.Name == name);
            tags.Add((name, value));
        }

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE Tags SYSTEM \"matroskatags.dtd\">\n<Tags>\n  <Tag>\n");
        xml.Append("    <Targets><TargetTypeValue>50</TargetTypeValue></Targets>\n");
        foreach (var (name, value) in tags.Where(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            xml.Append($"    <Simple><Name>{SecurityElement.Escape(name)}</Name><String>{SecurityElement.Escape(value)}</String></Simple>\n");
        }

        xml.Append("  </Tag>\n</Tags>\n");
        return xml.ToString();
    }

    /// <summary>
    /// mkvmerge's arguments: one input per demuxed file, each with its language, name and
    /// flags, then the order. The raw video gets the disc's frame rate, since an elementary
    /// stream does not always carry it.
    /// </summary>
    public static List<string> MkvmergeArguments(string output, string title, IReadOnlyList<OutputTrack> outputs,
        string? chaptersPath, string? tagsPath, (int Numerator, int Denominator)? frameRate, string? videoPath = null)
    {
        var arguments = new List<string> { "--gui-mode", "-o", output, "--title", title };
        if (tagsPath != null)
        {
            arguments.AddRange(["--global-tags", tagsPath]);
        }

        if (chaptersPath != null)
        {
            arguments.AddRange(["--chapters", chaptersPath]);
        }

        foreach (var track in outputs)
        {
            var planned = track.Planned;
            arguments.AddRange(["--language", $"0:{planned.Language}"]);
            arguments.AddRange(["--track-name", $"0:{track.Name}"]);
            arguments.AddRange(["--default-track-flag", $"0:{(planned.Default ? "yes" : "no")}"]);
            if (planned.Kind == StreamKind.Subtitle)
            {
                arguments.AddRange(["--forced-display-flag", $"0:{(planned.Forced ? "yes" : "no")}"]);
            }

            if (planned.Commentary)
            {
                arguments.AddRange(["--commentary-flag", "0:yes"]);
            }

            if (planned.HearingImpaired)
            {
                arguments.AddRange(["--hearing-impaired-flag", "0:yes"]);
            }

            if (planned.VisualImpaired)
            {
                arguments.AddRange(["--visual-impaired-flag", "0:yes"]);
            }

            if (planned.Kind == StreamKind.Video && frameRate is { } rate)
            {
                arguments.AddRange(["--default-duration", $"0:{rate.Numerator}/{rate.Denominator}p"]);
            }

            if (track.File.DelayMs is { } delay)
            {
                arguments.AddRange(["--sync", $"0:{delay}"]);
            }

            arguments.Add(planned.Kind == StreamKind.Video && videoPath != null ? videoPath : track.File.Path);
        }

        if (outputs.Count > 1)
        {
            arguments.AddRange(["--track-order", string.Join(',', outputs.Select((_, i) => $"{i}:0"))]);
        }

        return arguments;
    }

    /// <summary>"#GUI#progress 45%" or "Progress: 45%".</summary>
    public static double? PercentProgress(string line) =>
        PercentRegex().Match(line) is { Success: true } m && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? Math.Clamp(p / 100, 0, 1)
            : null;

    private static string Stem(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    private static bool HoldsPid(string fileName, int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        var hex = pid.ToString("x4", CultureInfo.InvariantCulture);
        var dec = pid.ToString(CultureInfo.InvariantCulture);
        return Regex.IsMatch(fileName, $@"(?<![0-9a-z])(0x)?({hex}|{dec})(?![0-9a-z])", RegexOptions.IgnoreCase);
    }

    private static string Plural(StreamKind kind) => kind switch
    {
        StreamKind.Video => "video",
        StreamKind.Audio => "audio",
        StreamKind.Subtitle => "subtitle",
        _ => "other"
    };

    [GeneratedRegex(@"delay\s*(-?\d+)\s*ms", RegexOptions.IgnoreCase)]
    private static partial Regex DelayRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%")]
    private static partial Regex PercentRegex();
}
