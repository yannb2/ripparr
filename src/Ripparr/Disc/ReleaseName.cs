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

    /// <summary>
    /// The country code in a full-disc release name, as ISO 3166 three letters: "Heat.1995.GBR.UHD.BluRay..." -> GBR.
    /// UK and GER are read as GBR and DEU. Null when the name has none (most do not).
    /// </summary>
    public static string? Country(string name)
    {
        var text = SeparatorRegex().Replace(name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name, " ");
        foreach (Match word in CountryRegex().Matches(text))
        {
            var code = word.Value.ToUpperInvariant();
            return CountryAliases.TryGetValue(code, out var real) ? real : code;
        }

        return null;
    }

    /// <summary>The country codes Ripparr knows, for the Country box (the same list as the rename-only custom formats).</summary>
    public static readonly string[] Countries =
        ["USA", "GBR", "DEU", "FRA", "ITA", "ESP", "NLD", "SWE", "NOR", "DNK", "FIN", "POL", "CZE", "AUS", "CAN", "JPN", "KOR", "HKG", "TWN", "CHN"];

    private static readonly Dictionary<string, string> CountryAliases = new(StringComparer.OrdinalIgnoreCase) { ["UK"] = "GBR", ["GER"] = "DEU" };

    /// <summary>
    /// Series and season from a TV disc's own title (BDMV/META): "Doctor Who - Season 1 - Disc 1" ->
    /// (Doctor Who, 1); "Sherlock: Series 2" -> (Sherlock, 2); a DVD's volume label DOCTOR_WHO_S1_D1 ->
    /// (Doctor Who, 1). Null when it names no season.
    /// </summary>
    public static (string Series, int Season)? Season(string? discTitle)
    {
        if (string.IsNullOrWhiteSpace(discTitle))
        {
            return null;
        }

        var match = SeasonRegex().Match(discTitle);
        if (!match.Success)
        {
            // A DVD's volume label: DOCTOR_WHO_S1_D1, FRIENDS_SEASON_2_DISC_3, The.Show.S01D2.
            var label = SeparatorRegex().Replace(discTitle, " ").Trim();
            match = SeasonRegex().Match(label) is { Success: true } words ? words : ShortSeasonRegex().Match(label);
            if (!match.Success)
            {
                return null;
            }

            var named = Tidy(match.Groups[1].Value.Trim(' ', '-', ':', ',', '–'));
            return named.Length == 0 ? null : (named, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        var series = match.Groups[1].Value.Trim(' ', '-', ':', ',', '–');
        return series.Length == 0 ? null : (series, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
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

    // Upper case only: "ita" or "can" in a title is a word, not a country.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(USA|GBR|UK|GER|DEU|FRA|ITA|ESP|NLD|SWE|NOR|DNK|FIN|POL|CZE|AUS|CAN|JPN|KOR|HKG|TWN|CHN)(?![A-Za-z0-9])")]
    private static partial Regex CountryRegex();

    [GeneratedRegex(@"[\s(\[]((?:19|20)\d{2})(?=[\s)\]]|$)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(2160p|1080p|720p|UHD|BluRay|Blu-ray|BDRemux|Remux|COMPLETE|HDR|DV)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"-([A-Za-z0-9][A-Za-z0-9_]{0,23})$")]
    private static partial Regex GroupRegex();

    [GeneratedRegex(@"^(.*?)[\s\-:,–]+(?:Season|Series|Saison|Staffel)\s*(\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"^(.+?)\s+S(\d{1,2})(?:\s*D(?:isc)?\s*\d{1,2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShortSeasonRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
}
