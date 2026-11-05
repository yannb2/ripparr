using System.Globalization;
using System.Text.RegularExpressions;

namespace Ripparr.Disc;

/// <summary>A first guess at title and year from a release name: "The.Matrix.1999.2160p.UHD.BluRay" -> The Matrix, 1999.</summary>
public static partial class ReleaseName
{
    public static (string Title, int? Year) Guess(string name)
    {
        var text = SeparatorRegex().Replace(name, " ").Trim();
        var match = YearRegex().Matches(text).LastOrDefault(m => m.Index > 0);
        if (match == null)
        {
            var cut = NoiseRegex().Match(text);
            return (Tidy(cut.Success && cut.Index > 0 ? text[..cut.Index] : text), null);
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return (Tidy(text[..match.Index]), year);
    }

    /// <summary>
    /// The group that put out a release, from its name: "Heat.1995.2160p.UHD.BluRay.HEVC.TrueHD-FraMeSToR" -> FraMeSToR.
    /// Null for a name that is not a release name (a disc label, "Heat (1995)") or ends in a format word (Blu-ray, WEB-DL).
    /// </summary>
    public static string? Group(string name)
    {
        var text = name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var match = GroupRegex().Match(text);
        if (!match.Success || !text[..match.Index].Contains('.'))
        {
            return null;
        }

        var group = match.Groups[1].Value;
        return NotGroups.Contains(group) || group.All(char.IsDigit) ? null : group;
    }

    // What follows a dash at the end of a name without being a group: Blu-ray, WEB-DL, BD-Rip, DTS-HD, DTS-X...
    private static readonly HashSet<string> NotGroups = new(StringComparer.OrdinalIgnoreCase)
        { "ray", "dl", "rip", "hd", "ma", "x", "es", "hr", "remux", "complete" };

    private static string Tidy(string title)
    {
        var text = SpacesRegex().Replace(title.Trim(' ', '-', '(', '['), " ");
        // SOME_FILM -> Some Film; mixed case is left as it is.
        return text.Any(char.IsLower) ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    [GeneratedRegex(@"[._]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"[\s(\[]((?:19|20)\d{2})(?=[\s)\]]|$)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(2160p|1080p|720p|UHD|BluRay|Blu-ray|BDRemux|Remux|COMPLETE|HDR|DV)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"-([A-Za-z0-9][A-Za-z0-9_]{0,23})$")]
    private static partial Regex GroupRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
}
