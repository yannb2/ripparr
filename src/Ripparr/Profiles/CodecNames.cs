namespace Ripparr.Profiles;

/// <summary>
/// What {codec} writes, per profile, as in the Muxarr fork: every detected codec label (Radarr's
/// words: TrueHD Atmos, DTS-HD MA, AC3...) can be given words of its own ("Dolby TrueHD"). The
/// video list also renames the HDR formats and Dolby Vision layers ({range}, {hdr}, {dvlayer}).
/// Only track names use them: the file name keeps Radarr's words.
/// </summary>
public static class CodecNames
{
    public static readonly IReadOnlyList<string> Audio =
    [
        "TrueHD", "TrueHD Atmos", "DTS-HD MA", "DTS-X", "DTS-HD HRA", "DTS-ES", "DTS", "EAC3", "EAC3 Atmos", "AC3",
        "PCM", "FLAC", "AAC", "Opus", "MP2"
    ];

    public static readonly IReadOnlyList<string> Video = ["HEVC", "AVC", "VC-1", "MPEG2", "AV1"];

    /// <summary>HDR formats and Dolby Vision layers, renamable like codecs.</summary>
    public static readonly IReadOnlyList<string> VideoFeatures = ["HDR10", "HDR10+", "HLG", "SDR", "FEL", "MEL"];

    public static readonly IReadOnlyList<string> Subtitle = ["PGS", "SRT"];

    /// <summary>
    /// The custom name of a label, else the label. A blank "TrueHD Atmos" (or "EAC3 Atmos") uses the
    /// custom name of its codec with "Atmos" added.
    /// </summary>
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
