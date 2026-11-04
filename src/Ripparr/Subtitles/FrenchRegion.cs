using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ripparr.Subtitles;

/// <summary>What the words of a French track point to: Québec, France, or nothing clear.</summary>
public sealed record RegionHint(string? Region, IReadOnlyList<string> Quebec, IReadOnlyList<string> France)
{
    /// <summary>"fin de semaine, courriel" for the page.</summary>
    public string Evidence => string.Join(", ", Region == "CA" ? Quebec : France);
}

/// <summary>
/// Guesses whether French subtitles were written in Québec or in France from words one side uses
/// and the other does not ("fin de semaine" / "week-end", "courriel" / "mail"). A Blu-ray only
/// labels both "fre". Needs the OCR text, and enough of it: a hint, never applied by itself.
/// </summary>
public static partial class FrenchRegion
{
    private static readonly string[] QuebecWords =
    [
        "fin de semaine", "fins de semaine", "courriel", "courriels", "stationnement", "magasiner", "magasinage",
        "presentement", "breuvage", "breuvages", "depanneur", "cellulaire", "pantoute", "icitte", "tabarnak",
        "tabarnac", "calisse", "ostie", "chum", "niaiser", "niaiseux", "achaler", "placoter", "pogner",
        "cegep", "traversier", "piastre", "piastres", "bec", "char"
    ];

    private static readonly string[] FranceWords =
    [
        "week-end", "week-ends", "weekend", "parking", "e-mail", "mail", "mails", "portable", "mec", "mecs",
        "nana", "nanas", "bagnole", "boulot", "putain", "faire du shopping", "ouf", "kiffer", "flic", "flics"
    ];

    /// <summary>At least this many different words of one side, and more than of the other.</summary>
    public const int Needed = 2;

    public static RegionHint Guess(IEnumerable<string> captions)
    {
        var text = " " + Plain(string.Join(" ", captions)) + " ";
        var quebec = QuebecWords.Where(w => Has(text, w)).Distinct().ToList();
        var france = FranceWords.Where(w => Has(text, w)).Distinct().ToList();
        // "bec" and "char" mean other things in France too: only as backing.
        var strongQuebec = quebec.Count(w => w is not ("bec" or "char"));
        var region = strongQuebec >= 1 && quebec.Count >= Needed && quebec.Count > france.Count ? "CA"
            : france.Count >= Needed && france.Count > quebec.Count ? "FR"
            : null;
        return new RegionHint(region, quebec, france);
    }

    private static bool Has(string text, string word) => text.Contains(" " + word + " ", StringComparison.Ordinal);

    /// <summary>Lower case, no accents, words separated by single spaces (hyphens kept).</summary>
    public static string Plain(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return NotWord().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"[^a-z0-9\-]+")]
    private static partial Regex NotWord();
}
