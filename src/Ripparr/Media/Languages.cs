using System.Globalization;

namespace Ripparr.Media;

/// <summary>English names for the ISO 639-2 codes stored in MKV and MP4 tracks.</summary>
public static class Languages
{
    // MKV uses the bibliographic codes (fre, ger, chi), .NET knows the terminology
    // ones (fra, deu, zho). These map one to the other.
    private static readonly Dictionary<string, string> BibliographicToTerminology = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho",
        ["cze"] = "ces", ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu",
        ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa",
        ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym"
    };

    private static readonly Dictionary<string, string> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["und"] = "Undetermined",
        ["mul"] = "Multiple",
        ["zxx"] = "No language",
        ["mis"] = "Uncoded",
        ["qaa"] = "Original"
    };

    private static readonly Lazy<Dictionary<string, string>> Names = new(Build);

    public static string Name(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Special["und"];
        }

        if (Special.TryGetValue(code, out var special))
        {
            return special;
        }

        var key = BibliographicToTerminology.GetValueOrDefault(code, code);
        if (Names.Value.TryGetValue(key, out var name))
        {
            return name;
        }

        // IETF tags such as "en-US" or two-letter codes.
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            if (!string.IsNullOrEmpty(culture.EnglishName) && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal))
            {
                return culture.EnglishName;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return code;
    }

    /// <summary>The code as the tables show it: "eng", "fre". Hover for the name.</summary>
    public static string Short(string? code)
    {
        return string.IsNullOrWhiteSpace(code) ? "und" : code.Trim().ToLowerInvariant();
    }

    /// <summary>True when two codes name the same language (fre and fra, ger and deu, fre and fr-CA).</summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(Base(a), Base(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The language without its region, as a terminology code: fr-CA, fr, fre and fra all give
    /// fra. A Blu-ray only says "fre"; the region (fr-CA) is set by hand on the job.
    /// </summary>
    public static string Base(string code)
    {
        var language = code.Trim();
        var dash = language.IndexOf('-');
        if (dash > 0)
        {
            language = language[..dash];
        }

        if (language.Length == 2)
        {
            try
            {
                var three = CultureInfo.GetCultureInfo(language).ThreeLetterISOLanguageName;
                if (three.Length == 3)
                {
                    language = three;
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }

        return BibliographicToTerminology.GetValueOrDefault(language, language).ToLowerInvariant();
    }

    /// <summary>The region of an IETF tag ("CA" in fr-CA), or null.</summary>
    public static string? Region(string? code)
    {
        var dash = code?.IndexOf('-') ?? -1;
        return dash > 0 ? code![(dash + 1)..].ToUpperInvariant() : null;
    }

    /// <summary>French as the disc gives it (fre) or as spoken in Québec (fr-CA): the job form's switch.</summary>
    public static bool IsFrench(string? code) => !string.IsNullOrWhiteSpace(code) && Base(code) == "fra";

    /// <summary>The code MKV uses (bibliographic: fre, ger) for a language name such as Radarr gives ("French").</summary>
    public static string? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = Names.Value.FirstOrDefault(n => string.Equals(n.Value, name.Trim(), StringComparison.OrdinalIgnoreCase)).Key;
        if (match == null)
        {
            return null;
        }

        var bibliographic = BibliographicToTerminology.FirstOrDefault(p => string.Equals(p.Value, match, StringComparison.OrdinalIgnoreCase)).Key;
        return (bibliographic ?? match).ToLowerInvariant();
    }

    /// <summary>The bibliographic form MKV uses: fra -> fre.</summary>
    public static string Bibliographic(string code)
    {
        var bibliographic = BibliographicToTerminology.FirstOrDefault(p => string.Equals(p.Value, code, StringComparison.OrdinalIgnoreCase)).Key;
        return (bibliographic ?? code).ToLowerInvariant();
    }

    /// <summary>A language to pick: the code MKV uses (fre, ger) and its English name.</summary>
    public sealed record Entry(string Code, string Name);

    private static readonly Lazy<List<Entry>> Entries = new(() => Names.Value
        .Select(n => new Entry(Bibliographic(n.Key), n.Value))
        .Where(e => !e.Name.Contains('(') && e.Code != "ivl")
        .GroupBy(e => e.Code).Select(g => g.First())
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>Languages whose name or code starts with the text, best matches first (an exact code, then names).</summary>
    public static List<Entry> Search(string? text, int count = 10)
    {
        var query = (text ?? "").Trim();
        if (query.Length == 0)
        {
            return [];
        }

        bool Code(Entry e) => e.Code.Equals(query, StringComparison.OrdinalIgnoreCase)
                              || Same(e.Code, query);
        return Entries.Value
            .Where(e => Code(e)
                        || e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                        || (query.Length > 2 && e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => Code(e) ? 0 : e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }

    private static Dictionary<string, string> Build()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var code = culture.ThreeLetterISOLanguageName;
            if (code.Length == 3 && !names.ContainsKey(code) && !string.IsNullOrEmpty(culture.EnglishName))
            {
                names[code] = culture.EnglishName;
            }
        }

        return names;
    }
}
