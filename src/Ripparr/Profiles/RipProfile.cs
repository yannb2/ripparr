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
public sealed class RipProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New profile";
    public string Description { get; set; } = "";

    /// <summary>The SOURCE tag and the last bracket of the file name (BDRemux, DVDRemux).</summary>
    public string Source { get; set; } = "BDRemux";

    // ---- DGDemux ----

    /// <summary>
    /// The DGDemux arguments for this profile, with {input} (the .mpls) and {output} (the file
    /// name prefix in the work folder). Empty = the global default, Ripparr__DgDemuxArguments.
    /// </summary>
    public string DgDemuxArguments { get; set; } = "";

    // ---- Video ----

    /// <summary>Keep the Dolby Vision enhancement layer: BL and EL are combined into one track (profile 7 FEL stays FEL).</summary>
    public bool KeepDolbyVisionLayer { get; set; } = true;

    /// <summary>"original" (the film's language), "und", or a code.</summary>
    public string VideoLanguage { get; set; } = "original";

    /// <summary>Tokens: {resolution} {codec} {hdr}. Empty = no name.</summary>
    public string VideoNameTemplate { get; set; } = "";

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

    /// <summary>Tokens: {language} {codec} {channels}.</summary>
    public string AudioNameTemplate { get; set; } = "{language} - {codec} {channels}";

    public string CommentaryNameTemplate { get; set; } = "{language} - Commentary - {codec} {channels}";

    // ---- Subtitles ----

    public List<string> SubtitleLanguages { get; set; } = ["eng", "fre"];

    public bool KeepOtherSubtitleLanguages { get; set; }

    /// <summary>Tokens: {language}.</summary>
    public string SubtitleNameTemplate { get; set; } = "{language}";

    public string ForcedSubtitleNameTemplate { get; set; } = "{language} - Forced";

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
            Description = "Everything that matters on a UHD disc: Dolby Vision kept as on the disc, lossless audio, commentaries."
        },
        new()
        {
            Id = "bd-remux",
            Name = "Blu-ray remux",
            Description = "A 1080p Blu-ray, lossless audio and commentaries, lossy duplicates dropped."
        }
    ];
}
