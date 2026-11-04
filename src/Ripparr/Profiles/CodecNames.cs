namespace Ripparr.Profiles;

/// <summary>
/// What {codec} writes, per profile, as in the Muxarr fork: every detected codec label (Radarr's
/// words: TrueHD Atmos, DTS-HD MA, AC3...) can be given words of its own ("Dolby TrueHD"). The
/// video list also renames the HDR formats and Dolby Vision layers ({range}, {hdr}, {dvlayer}).
/// Only track names use them: the file name keeps Radarr's words.
/// </summary>
public static class CodecNames
{
    /// <summary>The audio labels, worded and ordered as in the Muxarr fork, so a list can be copied across.</summary>
    public static readonly IReadOnlyList<string> Audio =
    [
        "AAC", "AC-3", "E-AC-3", "E-AC-3 Atmos", "DTS", "DTS-ES", "DTS 96/24", "DTS-HD High Resolution Audio",
        "DTS-HD Master Audio", "DTS:X", "DTS Express", "TrueHD", "TrueHD Atmos", "FLAC", "Opus", "Vorbis", "MP3", "MP2", "PCM"
    ];

    /// <summary>Radarr's words, which Ripparr detects and the file name keeps, and the Muxarr label each one is listed as.</summary>
    private static readonly Dictionary<string, string> MuxarrLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AC3"] = "AC-3",
        ["EAC3"] = "E-AC-3",
        ["EAC3 Atmos"] = "E-AC-3 Atmos",
        ["DTS-HD MA"] = "DTS-HD Master Audio",
        ["DTS-HD HRA"] = "DTS-HD High Resolution Audio",
        ["DTS-X"] = "DTS:X",
        ["DTS-HD LBR"] = "DTS Express",
        ["DTS Express"] = "DTS Express",
        ["DTS 96/24"] = "DTS 96/24"
    };

    /// <summary>The Muxarr label for a detected audio codec ("AC3" → "AC-3"); other labels as they are.</summary>
    public static string Label(string label) => MuxarrLabels.GetValueOrDefault(label, label);

    /// <summary>
    /// A profile saved before 0.4.11 keyed its audio names with Radarr's words ("AC3"): they move to
    /// the Muxarr labels ("AC-3"), keeping a name already set under the new label.
    /// </summary>
    public static void MoveToMuxarrLabels(Dictionary<string, string> names)
    {
        foreach (var key in names.Keys.ToList())
        {
            var label = Label(key);
            if (label == key)
            {
                continue;
            }

            var value = names[key];
            names.Remove(key);
            if (!string.IsNullOrWhiteSpace(value) && !names.Keys.Any(k => string.Equals(k, label, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(names[k])))
            {
                names[label] = value;
            }
        }
    }

    public static readonly IReadOnlyList<string> Video = ["HEVC", "AVC", "VC-1", "MPEG2", "AV1"];

    /// <summary>HDR formats and Dolby Vision layers, renamable like codecs.</summary>
    public static readonly IReadOnlyList<string> VideoFeatures = ["HDR10", "HDR10+", "HLG", "SDR", "FEL", "MEL"];

    public static readonly IReadOnlyList<string> Subtitle = ["PGS", "SRT"];

    /// <summary>
    /// The custom name of a label, else the label. A blank "TrueHD Atmos" (or "E-AC-3 Atmos") uses the
    /// custom name of its codec with "Atmos" added. Audio labels are the Muxarr fork's ("AC-3",
    /// "DTS-HD Master Audio"), as {codec} writes when nothing is set, like Muxarr does.
    /// </summary>
    public static string RenameAudio(string? label, IReadOnlyDictionary<string, string>? names) =>
        Rename(string.IsNullOrEmpty(label) ? label : Label(label), names);

    public static string Rename(string? label, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrEmpty(label))
        {
            return label ?? "";
        }

        if (names == null || names.Count == 0)
        {
            return label;
        }

        if (TryGet(names, label, out var custom))
        {
            return custom;
        }

        const string atmos = " Atmos";
        return label.EndsWith(atmos, StringComparison.Ordinal) && TryGet(names, label[..^atmos.Length], out var core)
            ? core + atmos
            : label;
    }

    /// <summary>Renames the HDR words in Radarr's {hdr} ("DV HDR10" with HDR10 → "DV HDR"), HDR10Plus as HDR10+.</summary>
    public static string RenameWords(string? text, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrEmpty(text) || names == null || names.Count == 0)
        {
            return text ?? "";
        }

        return string.Join(' ', text.Split(' ').Select(word =>
        {
            var label = word == "HDR10Plus" ? "HDR10+" : word;
            return TryGet(names, label, out var custom) ? custom : word;
        }));
    }

    /// <summary>Profiles round-trip through JSON, which drops a dictionary's comparer: keys match without case.</summary>
    private static bool TryGet(IReadOnlyDictionary<string, string> names, string key, out string value)
    {
        foreach (var (k, v) in names)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
            {
                value = v.Trim();
                return true;
            }
        }

        value = "";
        return false;
    }
}
