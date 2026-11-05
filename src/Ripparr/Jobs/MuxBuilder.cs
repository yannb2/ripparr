using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Jobs;

/// <summary>
/// A file DGDemux wrote, with what mediainfo read from it; or, with <see cref="TrackId"/>, one
/// track of an MKV (a DVD title MakeMKV ripped), which mkvmerge takes from that file.
/// </summary>
public sealed record DemuxedFile(string Path, StreamFacts Facts, int? TrackId = null)
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
            var captions = size == 0 ? [] : Arrkit.Subtitles.PgsReader.Read(path);
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
    /// The track name, from the profile's templates; the text typed for the track on the form is
    /// their {description}. The video's Dolby Vision profile and layer come from dovi_tool once the
    /// layers are merged.
    /// </summary>
    public static string TrackName(PlannedTrack track, StreamFacts facts, RipProfile profile, string? originalLanguage = null,
        string? dvProfile = null, string? dvLayer = null)
    {
        var typed = string.IsNullOrWhiteSpace(track.Name) ? null : track.Name.Trim();
        var language = Languages.Name(track.Language);
        var flags = Flags(track, originalLanguage);
        return track.Kind switch
        {
            StreamKind.Video => TrackNaming.Video(profile, language, facts.Resolution, facts.VideoCodec, facts.DynamicRange,
                TrackNaming.DolbyVisionProfile(dvProfile ?? facts.DolbyVisionProfile, facts.DynamicRange), dvLayer, typed),
            StreamKind.Audio => TrackNaming.Audio(profile, language,
                string.IsNullOrEmpty(facts.Format) ? track.Codec : facts.AudioCodec, facts.ChannelWords, flags, typed),
            StreamKind.Subtitle => TrackNaming.Subtitle(profile, language, SubtitleCodec(track, facts), flags, typed),
            _ => ""
        };
    }

    /// <summary>PGS for Blu-ray picture subtitles, SRT for the OCR text.</summary>
    public static string SubtitleCodec(PlannedTrack track, StreamFacts facts) =>
        string.Equals(facts.Format, "SRT (OCR)", StringComparison.Ordinal) || track.Codec == "SRT" ? "SRT" : string.IsNullOrEmpty(track.Codec) ? "PGS" : track.Codec;

    /// <summary>The base layer's facts once its enhancement layer and RPU are merged in: Dolby Vision, BL+EL+RPU.</summary>
    public static StreamFacts WithEnhancementLayer(StreamFacts facts) => facts.DolbyVision
        ? facts
        : facts with
        {
            HdrFormat = string.IsNullOrEmpty(facts.HdrFormat) ? "Dolby Vision" : $"Dolby Vision / {facts.HdrFormat}",
            HdrSettings = string.IsNullOrEmpty(facts.HdrSettings) ? "BL+EL+RPU" : $"BL+EL+RPU / {facts.HdrSettings}"
        };

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
            // A 2160p SDR film gets [SDR], as the rename-only custom format does in Radarr; below 4K, nothing.
            ["hdr"] = video == null ? null : string.IsNullOrEmpty(range) ? TrackNaming.SdrOnlyAt2160p("SDR", video.Resolution) : range,
            ["audio"] = audioWords,
            ["source"] = SourceInName(SourceWord(job)),
            ["country"] = string.IsNullOrWhiteSpace(job.Country) ? null : job.Country.Trim().ToUpperInvariant(),
            ["episode"] = job.EpisodeCode,
            ["absolute"] = job.AbsoluteEpisode is { } absolute ? EpisodeNumbers.Absolute(absolute) : null,
            ["episodetitle"] = job.EpisodeTitle,
            // "-GROUP" at the end of the name: how Radarr and Sonarr read the release group on import.
            ["releasegroup"] = ReleaseGroupWord(GlobalTag.Find(job.Tags, GlobalTag.ReleaseGroup))
        };
    }

    /// <summary>The SOURCE tag and the file name's source word: the profile's, or its DVD one for a DVD (DVD Remux).</summary>
    public static string SourceWord(Job job) =>
        job.Dvd && !string.IsNullOrWhiteSpace(job.Profile.DvdSource) ? job.Profile.DvdSource.Trim() : job.Profile.Source;

    /// <summary>
    /// The source as the file name writes it: "BD Remux" for BDRemux, "DVD Remux" for DVDRemux. Sonarr reads "[BDRemux]" as HDTV
    /// (its remux pattern wants a space, dot or underscore before the word, and "BD" alone is its Blu-ray
    /// source); "[BD Remux]" is Bluray Remux in Sonarr and Radarr alike. Their rename on import writes
    /// the library's [BDRemux] again, from the quality. The SOURCE tag keeps the profile's word.
    /// </summary>
    public static string? SourceInName(string? source) => source?.Trim().ToUpperInvariant() switch
    {
        "BDREMUX" => "BD Remux",
        // Radarr and Sonarr read DVD only when a space follows it; DVDRemux would be no source at all.
        "DVDREMUX" => "DVD Remux",
        _ => source
    };

    /// <summary>The release group as one word for a file name: Radarr stops reading it at a space.</summary>
    public static string? ReleaseGroupWord(string? group) =>
        string.IsNullOrWhiteSpace(group) ? null : string.Join('.', group.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Chapters as an OGM text file mkvmerge reads: CHAPTER01=00:00:00.000 / CHAPTER01NAME=Chapter 01.
    /// A name typed on the disc page wins over the profile's naming.
    /// </summary>
    public static string ChapterFile(Mpls playlist, ChapterNaming naming, IReadOnlyDictionary<int, string>? names = null)
    {
        var text = new StringBuilder();
        var number = 0;
        foreach (var mark in playlist.Chapters)
        {
            number++;
            var id = number.ToString("00", CultureInfo.InvariantCulture);
            text.Append($"CHAPTER{id}={mark.Start:hh\\:mm\\:ss\\.fff}\n");
            text.Append($"CHAPTER{id}NAME={ChapterName(number, naming, names)}\n");
        }

        return text.ToString();
    }

    /// <summary>The name chapter <paramref name="number"/> gets: the typed one, else "Chapter 01" or none as the profile says.</summary>
    public static string ChapterName(int number, ChapterNaming naming, IReadOnlyDictionary<int, string>? names = null)
    {
        if (names != null && names.TryGetValue(number, out var typed) && !string.IsNullOrWhiteSpace(typed))
        {
            // One line per name in the file.
            return typed.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        return naming == ChapterNaming.Numbered ? $"Chapter {number.ToString("00", CultureInfo.InvariantCulture)}" : "";
    }

    /// <summary>MKV global tags (TargetTypeValue 50), the same names tag_release.py writes.</summary>
    public static string TagsXml(Job job, IReadOnlyList<OutputTrack> outputs)
    {
        var video = outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Video)?.File.Facts;
        var tags = new List<(string Name, string? Value)>
        {
            ("TMDB", job.TmdbId is { } tmdb ? $"{(job.IsEpisode ? "tv" : "movie")}/{tmdb}" : null),
            ("IMDB", job.ImdbId),
            ("TVDB2", job.TvdbId is { } tvdb ? $"series/{tvdb}" : null),
            ("ABSOLUTE_EPISODE", job.AbsoluteEpisode?.ToString(CultureInfo.InvariantCulture)),
            ("RELEASE_NAME", job.SourceName),
            ("SOURCE_RELEASE_GROUP", string.IsNullOrWhiteSpace(job.SourceReleaseGroup) ? null : job.SourceReleaseGroup.Trim()),
            ("DISC_TITLE", string.IsNullOrWhiteSpace(job.DiscTitle) ? null : job.DiscTitle.Trim()),
            ("DISC_COUNTRY", string.IsNullOrWhiteSpace(job.Country) ? null : job.Country.Trim().ToUpperInvariant()),
            ("SOURCE", SourceWord(job)),
            ("VIDEO_CODEC", video?.VideoCodec),
            // Spelled as tag_release.py does on import (HDR10+, "7 FEL"), so the values stay the same; the file name keeps Radarr's HDR10Plus.
            ("HDR_FORMAT", video?.DynamicRange is { Length: > 0 } hdr ? hdr.Replace("HDR10Plus", "HDR10+") : null),
            ("DV_PROFILE", video is { DolbyVision: true } ? job.DolbyVisionProfile ?? video.DolbyVisionProfile : null),
            ("EDITION", string.IsNullOrWhiteSpace(job.Edition) ? null : job.Edition),
            ("LABEL", string.IsNullOrWhiteSpace(job.ReleaseLabel) ? null : job.ReleaseLabel.Trim()),
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
            // The file is written as UTF-8: say so, or typed names with accents depend on the locale.
            arguments.AddRange(["--chapter-charset", "UTF-8", "--chapters", chaptersPath]);
        }

        foreach (var track in outputs)
        {
            var planned = track.Planned;
            var id = track.File.TrackId ?? 0;
            // One track of an MKV: only that one, and none of the file's chapters, tags or
            // attachments (the job writes its own); its flags are set both ways, the MKV has some.
            var fromMkv = track.File.TrackId != null;
            if (fromMkv)
            {
                arguments.AddRange(planned.Kind switch
                {
                    StreamKind.Video => ["--video-tracks", $"{id}", "--no-audio", "--no-subtitles"],
                    StreamKind.Audio => ["--audio-tracks", $"{id}", "--no-video", "--no-subtitles"],
                    _ => ["--subtitle-tracks", $"{id}", "--no-video", "--no-audio"]
                });
                arguments.AddRange(["--no-chapters", "--no-global-tags", "--no-track-tags", "--no-attachments", "--no-buttons"]);
            }

            arguments.AddRange(["--language", $"{id}:{planned.Language}"]);
            arguments.AddRange(["--track-name", $"{id}:{track.Name}"]);
            arguments.AddRange(["--default-track-flag", $"{id}:{(planned.Default ? "yes" : "no")}"]);
            if (planned.Kind == StreamKind.Subtitle)
            {
                arguments.AddRange(["--forced-display-flag", $"{id}:{(planned.Forced ? "yes" : "no")}"]);
            }

            if (planned.Commentary || fromMkv)
            {
                arguments.AddRange(["--commentary-flag", $"{id}:{(planned.Commentary ? "yes" : "no")}"]);
            }

            if (planned.HearingImpaired || fromMkv)
            {
                arguments.AddRange(["--hearing-impaired-flag", $"{id}:{(planned.HearingImpaired ? "yes" : "no")}"]);
            }

            if (planned.VisualImpaired || fromMkv)
            {
                arguments.AddRange(["--visual-impaired-flag", $"{id}:{(planned.VisualImpaired ? "yes" : "no")}"]);
            }

            if (planned.Kind == StreamKind.Video && frameRate is { } rate)
            {
                arguments.AddRange(["--default-duration", $"{id}:{rate.Numerator}/{rate.Denominator}p"]);
            }

            if (track.File.DelayMs is { } delay)
            {
                arguments.AddRange(["--sync", $"{id}:{delay}"]);
            }

            arguments.Add(planned.Kind == StreamKind.Video && videoPath != null ? videoPath : track.File.Path);
        }

        if (outputs.Count > 1)
        {
            arguments.AddRange(["--track-order", string.Join(',', outputs.Select((o, i) =>
                $"{i}:{(o.Planned.Kind == StreamKind.Video && videoPath != null ? 0 : o.File.TrackId ?? 0)}"))]);
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
