using System.Text.Json.Serialization;

namespace Ripparr.Profiles;

public enum ChapterNaming
{
    /// <summary>Chapter 01, Chapter 02...</summary>
    Numbered,

    /// <summary>No names, only the times.</summary>
    None
}

/// <summary>
/// Which playlists and tracks a job keeps and how it names everything. Stored as JSON,
/// so a new setting needs no database change; a missing value takes its default.
/// </summary>
/// <summary>An MKV global tag: name (upper case, as Matroska writes them) and value, which may hold name placeholders.</summary>
public sealed class GlobalTag
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>Matroska tag names are upper case with underscores: "release group" becomes RELEASE_GROUP.</summary>
    public static string Normalise(string? name) =>
        string.Join('_', (name ?? "").Trim().ToUpperInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries));

    public GlobalTag Copy() => new() { Name = Name, Value = Value };
}

public sealed class RipProfile : IJsonOnDeserialized
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New profile";
    public string Description { get; set; } = "";

    /// <summary>The SOURCE tag and the last bracket of the file name (BDRemux, DVDRemux).</summary>
    public string Source { get; set; } = "BDRemux";

    // ---- DGDemux ----

    /// <summary>The DGDemux switches (-demux with the kept streams, -sf, -sp...).</summary>
    public DgDemuxSettings DgDemux { get; set; } = new();

    /// <summary>
    /// Extra DGDemux arguments, added after the switches. A full template with {input} (the
    /// .mpls) and {output} (the file name prefix) replaces the global template and the switches.
    /// </summary>
    public string DgDemuxArguments { get; set; } = "";

    // ---- Video ----

    // The Dolby Vision enhancement layer is always merged into the video with dovi_tool
    // (KeepDolbyVisionLayer is gone since 0.2.2; saved profiles that still have it are fine).

    /// <summary>"original" (the film's language), "und", or a code.</summary>
    public string VideoLanguage { get; set; } = "original";

    /// <summary>
    /// Tokens: {resolution} {codec} {dv} {dvprofile} {dvlayer} {range} {hdr} {language}. Empty = no name.
    /// </summary>
    public string VideoNameTemplate { get; set; } = TrackNaming.DefaultVideo;

    /// <summary>What {dv} writes, with {dvprofile} and {dvlayer}. Empty = "Dolby Vision Profile {dvprofile} {dvlayer}".</summary>
    public string DolbyVisionTemplate { get; set; } = "";

    // ---- Audio ----

    /// <summary>Kept languages, in order. "original" stands for the film's own language.</summary>
    public List<string> AudioLanguages { get; set; } = ["original", "eng", "fre"];

    public bool KeepOtherAudioLanguages { get; set; }

    /// <summary>Keep tracks that look like a commentary (stereo, after the film's own tracks).</summary>
    public bool KeepCommentary { get; set; } = true;

    /// <summary>Drop a lossy track (AC3, EAC3, DTS) when a lossless one (TrueHD, DTS-HD MA, PCM) exists in the same language.</summary>
    public bool DropLossyWhenLossless { get; set; } = true;

    /// <summary>Keep the AC-3 core DGDemux writes next to a TrueHD track.</summary>
    public bool KeepTrueHdCore { get; set; }

    /// <summary>Tokens: {language} {codec} {channels} {dub} {description} {trackname} and the flag words.</summary>
    public string AudioNameTemplate { get; set; } = TrackNaming.DefaultAudio;

    /// <summary>Templates for flagged tracks, by flag (sdh, forced, commentary, ad, dub). The first matching flag wins.</summary>
    public Dictionary<string, string> AudioNameOverrides { get; set; } = TrackNaming.DefaultAudioOverrides();

    // ---- Subtitles ----

    public List<string> SubtitleLanguages { get; set; } = ["eng", "fre"];

    public bool KeepOtherSubtitleLanguages { get; set; }

    /// <summary>Tokens: {language} {codec} {dub} {trackname} and the flag words. SRT tracks use it too, with {codec} = SRT.</summary>
    public string SubtitleNameTemplate { get; set; } = TrackNaming.DefaultSubtitle;

    /// <summary>Templates for flagged tracks, by flag (sdh, forced, commentary, ad, dub). The first matching flag wins.</summary>
    public Dictionary<string, string> SubtitleNameOverrides { get; set; } = TrackNaming.DefaultSubtitleOverrides();

    /// <summary>
    /// Languages that also get a text (SRT) track, read from the picture subtitles with OCR
    /// (Tesseract). Empty = no SRT tracks. "original" stands for the film's own language.
    /// </summary>
    public List<string> SrtLanguages { get; set; } = [];

    /// <summary>Remove the text for the deaf and hard of hearing from the SRT tracks ([door slams], JOHN:, ♪ lyrics).</summary>
    public bool SrtRemoveSdh { get; set; } = true;

    /// <summary>Mark a subtitle track as SDH (flag and name) when its OCR finds SDH text in many captions.</summary>
    public bool DetectSdh { get; set; } = true;

    /// <summary>Also make an SRT of forced subtitle tracks.</summary>
    public bool SrtForForced { get; set; } = true;


    // ---- Chapters ----

    public ChapterNaming Chapters { get; set; } = ChapterNaming.Numbered;

    // ---- Output ----

    /// <summary>
    /// Tokens: {title} {year} {edition} {resolution} {videocodec} {hdr} {audio} {source}.
    /// Text inside the braces is kept only when the token has a value: "{ - edition}".
    /// </summary>
    public string FileNameTemplate { get; set; } =
        "{title} ({year}){ - edition} [{resolution}][{videocodec}][{hdr}][{audio}][{source}]";

    /// <summary>The MKV title. Same tokens.</summary>
    public string TitleTemplate { get; set; } = "{title} ({year}){ - edition}";

    /// <summary>Write TMDB, IMDB, SOURCE, RELEASE_NAME, EDITION... as MKV global tags, as tag_release does.</summary>
    public bool WriteTags { get; set; } = true;

    /// <summary>
    /// Global tags of your own (RELEASE_GROUP, ENCODED_BY...), with the value a new job starts with.
    /// Each job can fill in or change the values; an empty value writes nothing. A tag with the
    /// name of an automatic one (SOURCE...) replaces it.
    /// </summary>
    public List<GlobalTag> CustomTags { get; set; } = [new() { Name = "RELEASE_GROUP" }];

    // ---- Names from before 0.3.0, read once and moved to the settings above ----

    /// <summary>Before 0.3.0: the commentary audio name. Now AudioNameOverrides["commentary"].</summary>
    public string? CommentaryNameTemplate { get; set; }

    /// <summary>Before 0.3.0: the forced subtitle name. Now SubtitleNameOverrides["forced"].</summary>
    public string? ForcedSubtitleNameTemplate { get; set; }

    /// <summary>Before 0.3.0: the SRT names. SRT tracks now use the subtitle names with {codec} = SRT.</summary>
    public string? SrtNameTemplate { get; set; }

    public string? ForcedSrtNameTemplate { get; set; }

    /// <summary>
    /// A profile saved before 0.3.0 (it still has the old names) moves to the 0.3.0 names: a name
    /// left at the old default takes the new default, a name of your own is kept.
    /// </summary>
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (CommentaryNameTemplate == null && ForcedSubtitleNameTemplate == null && SrtNameTemplate == null && ForcedSrtNameTemplate == null)
        {
            return;
        }

        if (VideoNameTemplate == "")
        {
            VideoNameTemplate = TrackNaming.DefaultVideo;
        }

        if (AudioNameTemplate == "{language} - {codec} {channels}")
        {
            AudioNameTemplate = TrackNaming.DefaultAudio;
        }

        if (CommentaryNameTemplate != null && CommentaryNameTemplate != "{language} - Commentary - {codec} {channels}")
        {
            AudioNameOverrides[TrackNaming.Commentary] = CommentaryNameTemplate;
        }

        if (SubtitleNameTemplate == "{language}")
        {
            SubtitleNameTemplate = TrackNaming.DefaultSubtitle;
        }

        if (ForcedSubtitleNameTemplate != null && ForcedSubtitleNameTemplate != "{language} - Forced")
        {
            SubtitleNameOverrides[TrackNaming.Forced] = ForcedSubtitleNameTemplate;
        }

        CommentaryNameTemplate = null;
        ForcedSubtitleNameTemplate = null;
        SrtNameTemplate = null;
        ForcedSrtNameTemplate = null;
    }

    public RipProfile Copy(string? name = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        var copy = System.Text.Json.JsonSerializer.Deserialize<RipProfile>(json)!;
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = name ?? $"{Name} (copy)";
        return copy;
    }

    /// <summary>The profiles a new install starts with.</summary>
    public static List<RipProfile> Defaults() =>
    [
        new()
        {
            Id = "uhd-remux",
            Name = "UHD remux",
            Description = "Everything that matters on a UHD disc: Dolby Vision merged into one track, lossless audio, commentaries."
        },
        new()
        {
            Id = "bd-remux",
            Name = "Blu-ray remux",
            Description = "A 1080p Blu-ray, lossless audio and commentaries, lossy duplicates dropped."
        },
        Extras()
    ];

    public const string ExtrasId = "extras";

    /// <summary>
    /// For featurettes, trailers and other extras: the film's language and English, no
    /// commentaries, no OCR. The file is named after the extra, not the film.
    /// </summary>
    public static RipProfile Extras() => new()
    {
        Id = ExtrasId,
        Name = "Extras",
        Description = "Featurettes, trailers, deleted scenes: the film's language and English, subtitles in English and French, no OCR.",
        AudioLanguages = ["original", "eng"],
        KeepCommentary = false,
        SubtitleLanguages = ["eng", "fre"],
        SrtLanguages = [],
        DetectSdh = false
    };
}
