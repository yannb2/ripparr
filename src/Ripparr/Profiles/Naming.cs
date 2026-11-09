using System.Text.RegularExpressions;

namespace Ripparr.Profiles;

/// <summary>Fills a name template such as "{title} ({year}){ - edition} [{resolution}]".</summary>
public static partial class Naming
{
    /// <summary>
    /// Replaces each {token} by its value. A brace group with text around the token,
    /// like "{ - edition}", is dropped as a whole when the token has no value. Empty
    /// brackets and doubled spaces left behind are removed.
    /// </summary>
    public static string Apply(string template, IReadOnlyDictionary<string, string?> values)
    {
        var text = TokenRegex().Replace(template, m =>
        {
            var key = m.Groups["name"].Value.ToLowerInvariant();
            if (!values.TryGetValue(key, out var value))
            {
                return m.Value; // not a token: leave it
            }

            return string.IsNullOrWhiteSpace(value) ? "" : m.Groups["before"].Value + value.Trim() + m.Groups["after"].Value;
        });

        text = EmptyBracketsRegex().Replace(text, "");
        text = SpacesRegex().Replace(text, " ");
        text = OneYear(text);
        return text.Trim().Trim('-').Trim();
    }

    /// <summary>
    /// "Doctor Who (2005) (2005)" becomes "Doctor Who (2005)": Sonarr and Radarr put the year in
    /// the title itself when another show or film has the same name, and "{title} ({year})" then
    /// adds it a second time.
    /// </summary>
    public static string OneYear(string text) => RepeatedYearRegex().Replace(text, "(${year})");

    /// <summary>"Title (Year)", without the year twice when the title already ends with it.</summary>
    public static string TitleAndYear(string title, int? year) =>
        year is { } y ? OneYear($"{title.Trim()} ({y})") : title.Trim();

    /// <summary>Removes what Windows, Samba and Jellyfin do not like in a file name.</summary>
    public static string SafeFileName(string name) => Arrkit.Library.FileNames.Safe(name);

    [GeneratedRegex(@"\{(?<before>[^{}A-Za-z]*)(?<name>[A-Za-z]+)(?<after>[^{}A-Za-z]*)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\[\s*\]|\(\s*\)")]
    private static partial Regex EmptyBracketsRegex();

    [GeneratedRegex(@"\((?<year>\d{4})\)\s*\(\k<year>\)")]
    private static partial Regex RepeatedYearRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
}
