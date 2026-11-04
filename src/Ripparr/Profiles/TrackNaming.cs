using System.Text.RegularExpressions;
using Ripparr.Media;

namespace Ripparr.Profiles;

/// <summary>A track's flags, in the order the name overrides are tried: the first matching flag wins.</summary>
public sealed record TrackFlags(bool HearingImpaired = false, bool Forced = false, bool Commentary = false, bool VisualImpaired = false, bool Dub = false);

/// <summary>
/// Track names, the same way as the Muxarr fork: one template per track type, plus a template
/// per flag (SDH, Forced, Commentary, AD, Dub) where the first matching flag wins.
/// </summary>
public static partial class TrackNaming
{
    public const string DefaultVideo = "{resolution} - {codec} - {dv} {range}";
    public const string DefaultDolbyVision = "Dolby Vision Profile {dvprofile} {dvlayer}";
    public const string DefaultAudio = "{language} - {codec} {channels} {dub}";
    public const string DefaultSubtitle = "{language} {dub} - {codec}";

    public const string HearingImpaired = "sdh";
    public const string Forced = "forced";
    public const string Commentary = "commentary";
    public const string VisualImpaired = "ad";
    public const string Dub = "dub";

    /// <summary>The flags in the order they are tried, with the words the profile page shows.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Icon, string Help)> Flags =
    [
        (HearingImpaired, "SDH", "bi-ear", "Subtitles for the deaf and hard of hearing"),
        (Forced, "Forced", "bi-lock", "Forced subtitles: only the foreign lines"),
        (Commentary, "Commentary", "bi-chat-quote", "A commentary track"),
        (VisualImpaired, "AD", "bi-eye", "Audio description for the visually impaired"),
        (Dub, "Dub", "bi-translate", "Audio in another language than the film's own")
    ];

    public static Dictionary<string, string> DefaultAudioOverrides() => new()
    {
        [Commentary] = "{language} - {codec} {channels} ({description})"
    };

    public static Dictionary<string, string> DefaultSubtitleOverrides() => new()
    {
        [HearingImpaired] = "{language} (SDH) - {codec}",
        [Forced] = "{language} (Forced) - {codec}",
        [Commentary] = "{language} (Commentary) - {description} - {codec}"
    };

    /// <summary>The template for a track: the first flag with an override, or the default.</summary>
    public static string Template(string fallback, IReadOnlyDictionary<string, string>? overrides, TrackFlags flags)
    {
        if (overrides != null)
        {
            foreach (var (key, _, _, _) in Flags)
            {
                if (Has(flags, key) && overrides.TryGetValue(key, out var template) && !string.IsNullOrWhiteSpace(template))
                {
                    return template;
                }
            }
        }

        return fallback;
    }

    public static bool Has(TrackFlags flags, string key) => key switch
    {
        HearingImpaired => flags.HearingImpaired,
        Forced => flags.Forced,
        Commentary => flags.Commentary,
        VisualImpaired => flags.VisualImpaired,
        Dub => flags.Dub,
        _ => false
    };

    /// <summary>The word values every track template can use: {forced} gives "Forced" on a forced track.</summary>
    /// <param name="description">The track's Description box on the disc page ({description}).</param>
    /// <param name="commentaryWord">With nothing typed, {description} of a commentary is "Commentary" (audio).</param>
    public static Dictionary<string, string?> FlagValues(TrackFlags flags, string? description = null, bool commentaryWord = true) => new()
    {
        ["hi"] = flags.HearingImpaired ? "SDH" : "",
        ["sdh"] = flags.HearingImpaired ? "SDH" : "",
        ["forced"] = flags.Forced ? "Forced" : "",
        ["commentary"] = flags.Commentary ? "Commentary" : "",
        ["visualimpaired"] = flags.VisualImpaired ? "AD" : "",
        ["ad"] = flags.VisualImpaired ? "AD" : "",
        ["dub"] = flags.Dub ? "Dub" : "",
        // A disc track has no name of its own: {description} is what the Description box says; a
        // commentary with nothing typed is described as one.
        ["description"] = string.IsNullOrWhiteSpace(description) ? flags.Commentary && commentaryWord ? "Commentary" : "" : description
    };

    public static string Audio(RipProfile profile, string language, string codec, string channels, TrackFlags flags, string? trackName = null)
    {
        var template = Template(profile.AudioNameTemplate, profile.AudioNameOverrides, flags);
        // A template that already says Commentary does not get it again from an empty {description}.
        var saysCommentary = Regex.Replace(template ?? "", @"\{[^}]*\}", "").Contains("commentary", StringComparison.OrdinalIgnoreCase);
        var values = FlagValues(flags, trackName, commentaryWord: !saysCommentary);
        values["language"] = language;
        values["codec"] = CodecNames.RenameAudio(codec, profile.AudioCodecNames);
        values["channels"] = channels;
        return Described(template, values, trackName);
    }

    public static string Subtitle(RipProfile profile, string language, string codec, TrackFlags flags, string? trackName = null)
    {
        // The subtitle commentary template says (Commentary) itself: {description} is only what was typed.
        var values = FlagValues(flags, trackName, commentaryWord: false);
        values["language"] = language;
        values["codec"] = CodecNames.Rename(codec, profile.SubtitleCodecNames);
        return Described(Template(profile.SubtitleNameTemplate, profile.SubtitleNameOverrides, flags), values, trackName);
    }

    public static string Video(RipProfile profile, string language, string resolution, string codec, string? dynamicRange,
        string? dvProfile, string? dvLayer, string? trackName = null)
    {
        return Described(profile.VideoNameTemplate, new Dictionary<string, string?>
        {
            ["description"] = trackName ?? "",
            ["language"] = language,
            ["resolution"] = resolution,
            ["codec"] = CodecNames.Rename(codec, profile.VideoCodecNames),
            ["hdr"] = CodecNames.RenameWords(dynamicRange, profile.VideoCodecNames),
            ["range"] = CodecNames.Rename(Range(dynamicRange, dvProfile != null), profile.VideoCodecNames),
            ["dv"] = DolbyVision(profile.DolbyVisionTemplate, dvProfile, CodecNames.Rename(dvLayer, profile.VideoCodecNames)),
            ["dvprofile"] = dvProfile,
            ["dvlayer"] = CodecNames.Rename(dvLayer, profile.VideoCodecNames)
        }, trackName);
    }

    /// <summary>
    /// The base layer's range, as the Muxarr fork's {range}: HDR10, HDR10+, HLG, or SDR for video
    /// with neither HDR nor Dolby Vision. Dolby Vision without an HDR base (profile 5) gives nothing.
    /// </summary>
    public static string Range(string? dynamicRange, bool dolbyVision)
    {
        var range = (dynamicRange ?? "").Replace("DV", "", StringComparison.Ordinal).Trim().Replace("HDR10Plus", "HDR10+", StringComparison.Ordinal);
        if (range.Length > 0)
        {
            return range;
        }

        return dolbyVision || (dynamicRange ?? "").Contains("DV", StringComparison.Ordinal) ? "" : "SDR";
    }

    /// <summary>The {dv} words: "Dolby Vision Profile 7 FEL", or "DV P7 FEL" from "DV P{dvprofile} {dvlayer}". Empty without Dolby Vision.</summary>
    public static string DolbyVision(string? template, string? dvProfile, string? dvLayer)
    {
        if (string.IsNullOrEmpty(dvProfile))
        {
            return "";
        }

        var text = (string.IsNullOrWhiteSpace(template) ? DefaultDolbyVision : template)
            .Replace("{dvprofile}", dvProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("{dvlayer}", dvLayer ?? "", StringComparison.OrdinalIgnoreCase);
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>
    /// Profile 8 with its compatibility: 8.1 (HDR10 base), 8.4 (HLG), 8.2 (SDR). Other profiles as they are.
    /// </summary>
    public static string? DolbyVisionProfile(string? profile, string? dynamicRange)
    {
        if (profile != "8")
        {
            return profile;
        }

        var range = Range(dynamicRange, dolbyVision: true);
        return range switch
        {
            "HLG" => "8.4",
            "" or "SDR" => "8.2",
            _ => "8.1"
        };
    }

    /// <summary>Naming.Apply, then the separators a missing value leaves behind ("English (Commentary) -  - PGS").</summary>
    /// <summary>
    /// A track template with the text typed for the track on the job (its Description box) as
    /// {description}. A template without it gets the text in brackets at the end, so it is
    /// not lost; text starting with "=" is the whole name, as typed.
    /// </summary>
    public static string Described(string template, IReadOnlyDictionary<string, string?> values, string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (text.StartsWith('='))
        {
            return text[1..].Trim();
        }

        var name = Apply(template, values);
        if (text.Length == 0 || UsesDescription(template))
        {
            return name;
        }

        return name.Length == 0 ? text : $"{name} ({text})";
    }

    /// <summary>Whether a template has a {description} token.</summary>
    public static bool UsesDescription(string template) =>
        System.Text.RegularExpressions.Regex.IsMatch(template, @"\{[^{}A-Za-z]*description[^{}A-Za-z]*\}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Apply(string template, IReadOnlyDictionary<string, string?> values)
    {
        var text = Naming.Apply(template, values);
        text = RepeatedDashes().Replace(text, " - ");
        return text.Trim().Trim('-').Trim();
    }

    /// <summary>A sample of each kind of track, for the profile page's preview.</summary>
    public static IEnumerable<(string What, string Name)> Preview(RipProfile profile)
    {
        yield return ("UHD, Dolby Vision FEL", Video(profile, "English", "2160p", "HEVC", "DV HDR10", "7", "FEL"));
        yield return ("UHD, HDR10", Video(profile, "English", "2160p", "HEVC", "HDR10", null, null));
        yield return ("Blu-ray", Video(profile, "English", "1080p", "AVC", "", null, null));
    }

    public static IEnumerable<(string What, string Name)> AudioPreview(RipProfile profile)
    {
        yield return ("Main", Audio(profile, "English", "TrueHD Atmos", "7.1", new TrackFlags()));
        yield return ("Dub", Audio(profile, "French", "DTS-HD MA", "5.1", new TrackFlags(Dub: true)));
        yield return ("Commentary", Audio(profile, "English", "AC3", "2.0", new TrackFlags(Commentary: true)));
        yield return ("Audio description", Audio(profile, "English", "AC3", "2.0", new TrackFlags(VisualImpaired: true)));
    }

    public static IEnumerable<(string What, string Name)> SubtitlePreview(RipProfile profile)
    {
        yield return ("Main", Subtitle(profile, "English", "PGS", new TrackFlags()));
        yield return ("SDH", Subtitle(profile, "English", "PGS", new TrackFlags(HearingImpaired: true)));
        yield return ("Forced", Subtitle(profile, "English", "PGS", new TrackFlags(Forced: true)));
        yield return ("Commentary", Subtitle(profile, "English", "PGS", new TrackFlags(Commentary: true)));
        yield return ("SRT by OCR", Subtitle(profile, "French", "SRT", new TrackFlags()));
    }

    [GeneratedRegex(@"\s+-(\s+-)+\s+")]
    private static partial Regex RepeatedDashes();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
