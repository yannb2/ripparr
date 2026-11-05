using System.Text;
using System.Text.RegularExpressions;

namespace Ripparr.Tools;

/// <summary>One line of makemkvcon's robot output (-r): MSG, PRGV, PRGT...</summary>
public sealed record RobotLine(string Type, IReadOnlyList<string> Values)
{
    /// <summary>The message text of a MSG line (its 4th value).</summary>
    public string? Message => Type == "MSG" && Values.Count >= 4 ? Values[3] : null;

    public int? Code => Values.Count > 0 && int.TryParse(Values[0], out var code) ? code : null;
}

/// <summary>Running makemkvcon, reading its robot output, and keeping its key in /config.</summary>
public static partial class MakeMkv
{
    /// <summary>Parses "MSG:1005,0,1,"MakeMKV v1.18.4 started","%1 started","..."" into its values.</summary>
    public static RobotLine? ParseRobot(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || colon > 6 || !line[..colon].All(char.IsAsciiLetterUpper))
        {
            return null;
        }

        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = colon + 1; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return new RobotLine(line[..colon], values);
    }

    /// <summary>PRGV:current,total,max -> total / max (the whole operation).</summary>
    public static double? Progress(RobotLine line)
    {
        if (line.Type != "PRGV" || line.Values.Count < 3
                                || !double.TryParse(line.Values[1], out var total)
                                || !double.TryParse(line.Values[2], out var max) || max <= 0)
        {
            return null;
        }

        return Math.Clamp(total / max, 0, 1);
    }

    /// <summary>The version in "MakeMKV v1.18.4 linux(x64-release) started".</summary>
    public static string? Version(string output) => VersionRegex().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// What makemkvcon says about its key and its age, in its own words: evaluation days,
    /// an expired key, a version too old. Read from the MSG lines of any run.
    /// </summary>
    public static List<string> KeyMessages(string output)
    {
        var messages = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = ParseRobot(raw.Trim());
            var text = line?.Message;
            if (text != null && KeyWordsRegex().IsMatch(text) && !messages.Contains(text))
            {
                messages.Add(text);
            }
        }

        return messages;
    }

    /// <summary>MakeMKV's settings file under the given home: $HOME/.MakeMKV/settings.conf.</summary>
    /// <summary>
    /// Whether the key works, from what makemkvcon printed when it started: no key, a key it
    /// took (no evaluation or expiry message), or one it refused (too old, expired, invalid,
    /// still in evaluation although a key is set).
    /// </summary>
    public static KeyCheck Assess(bool hasKey, bool ran, IReadOnlyList<string> messages)
    {
        if (!ran)
        {
            return new KeyCheck(KeyStatus.Unknown, "makemkvcon did not run, so the key could not be checked.");
        }

        string? Find(Regex regex) => messages.FirstOrDefault(m => regex.IsMatch(m));
        if (Find(InvalidRegex()) is { } invalid)
        {
            return new KeyCheck(KeyStatus.Bad, $"MakeMKV refused the key: {invalid}");
        }

        if (Find(TooOldRegex()) is not null)
        {
            return new KeyCheck(KeyStatus.Bad, hasKey
                ? "MakeMKV refused the key (\"version too old\"): the key has expired. Beta keys last about a month."
                : "MakeMKV needs a key (\"version too old\").");
        }

        if (Find(ExpiredRegex()) is not null)
        {
            return new KeyCheck(KeyStatus.Bad, hasKey ? "The key has expired." : "The evaluation period is over: MakeMKV needs a key.");
        }

        if (Find(EvaluationRegex()) is { } evaluation)
        {
            return hasKey
                ? new KeyCheck(KeyStatus.Bad, $"MakeMKV did not take the key and still runs as an evaluation: {evaluation}")
                : new KeyCheck(KeyStatus.Evaluation, evaluation);
        }

        return hasKey
            ? new KeyCheck(KeyStatus.Good, "Key accepted: MakeMKV started with no evaluation or expiry message.")
            : new KeyCheck(KeyStatus.Missing, "No key set.");
    }

    public static string SettingsPath(string home) => Path.Combine(home, ".MakeMKV", "settings.conf");

    public static string? ReadKey(string home)
    {
        var path = SettingsPath(home);
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var match = KeyLineRegex().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>Writes app_Key, keeping every other line of the file. A null key removes it.</summary>
    public static void WriteKey(string home, string? key) => WriteSetting(home, "app_Key", string.IsNullOrWhiteSpace(key) ? null : key.Trim());

    /// <summary>Sets one line of settings.conf (name = "value"), keeping every other line. A null value removes it.</summary>
    public static void WriteSetting(string home, string name, string? value)
    {
        var path = SettingsPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var regex = new Regex($@"^\s*{Regex.Escape(name)}\s*=");
        lines.RemoveAll(l => regex.IsMatch(l));
        if (value != null)
        {
            lines.Add($"{name} = \"{value}\"");
        }

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// MakeMKV's track selection for "mkv": every track. Its default keeps only some languages,
    /// and Ripparr's own plan decides which tracks go in the finished file.
    /// </summary>
    public const string SelectAll = "+sel:all";

    /// <summary>A title in makemkvcon's robot "info" output: its id for "mkv" and what it says about it.</summary>
    public sealed record Title(int Id, TimeSpan Duration, int Chapters, int? OriginalTitle, string? SourceFile, long Bytes);

    /// <summary>The titles of TINFO lines: 9 = length (h:mm:ss), 8 = chapters, 24 = the disc's own title number, 16 = source file, 11 = bytes.</summary>
    public static List<Title> Titles(string output)
    {
        var values = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var raw in output.Split('\n'))
        {
            if (ParseRobot(raw.Trim()) is not { Type: "TINFO", Values.Count: >= 4 } line || !int.TryParse(line.Values[0], out var id))
            {
                continue;
            }

            if (!values.TryGetValue(id, out var title))
            {
                title = new Dictionary<string, string>();
                values[id] = title;
            }

            title[line.Values[1]] = line.Values[3];
        }

        return values.Select(v => new Title(
            v.Key,
            TimeSpan.TryParse(v.Value.GetValueOrDefault("9"), System.Globalization.CultureInfo.InvariantCulture, out var length) ? length : TimeSpan.Zero,
            int.TryParse(v.Value.GetValueOrDefault("8"), out var chapters) ? chapters : 0,
            int.TryParse(v.Value.GetValueOrDefault("24"), out var original) ? original : null,
            v.Value.GetValueOrDefault("16"),
            long.TryParse(v.Value.GetValueOrDefault("11"), out var bytes) ? bytes : 0)).ToList();
    }

    /// <summary>
    /// MakeMKV's title for a DVD title: the one whose original title number (TINFO 24) is the
    /// DVD's, when its length is within 2% (MakeMKV's lengths can be a few seconds off the IFO's:
    /// 1:15:46 for an IFO's 1:15:48); otherwise one of about the same length (5 s, or 0.5% of a
    /// long title), the same number of chapters first, and among titles of the same length and
    /// chapters, the same place in the order. Null when none fits.
    /// </summary>
    public static Title? DvdTitle(IReadOnlyList<Title> titles, Disc.DvdTitle wanted, IReadOnlyList<Disc.DvdTitle> all)
    {
        static bool Within(TimeSpan a, TimeSpan b, double seconds, double share) =>
            Math.Abs((a - b).TotalSeconds) <= Math.Max(seconds, b.TotalSeconds * share);

        if (titles.FirstOrDefault(t => t.OriginalTitle == wanted.Number && Within(t.Duration, wanted.Duration, 10, 0.02)) is { } numbered)
        {
            return numbered;
        }

        bool Near(TimeSpan a, TimeSpan b) => Within(a, b, 5, 0.005);
        var candidates = titles.Where(t => Near(t.Duration, wanted.Duration)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var sameChapters = candidates.Where(t => t.Chapters == wanted.Chapters.Count).ToList();
        if (sameChapters.Count > 0)
        {
            candidates = sameChapters;
        }

        var twins = all.Where(t => Near(t.Duration, wanted.Duration) && (sameChapters.Count == 0 || t.Chapters.Count == wanted.Chapters.Count))
            .OrderBy(t => t.Number).ToList();
        var place = Math.Max(0, twins.FindIndex(t => t.Number == wanted.Number));
        return candidates[Math.Min(place, candidates.Count - 1)];
    }

    /// <summary>"T-abc...xyz" -> "T-abc…xyz", for the page.</summary>
    public static string Mask(string key) => key.Length <= 10 ? "••••" : $"{key[..5]}…{key[^4..]}";

    /// <summary>A key as MakeMKV prints them: "T-" (beta) or "M-" and a long run of letters, digits and @.</summary>
    public static bool LooksLikeKey(string? key) => key != null && KeyFormatRegex().IsMatch(key.Trim());

    /// <summary>Finds the beta key on the forum page that publishes it (the first T- key in a code block).</summary>
    public static string? FindBetaKey(string html) => BetaKeyRegex().Match(html) is { Success: true } m ? m.Value : null;

    /// <summary>The source argument makemkvcon takes: iso:/path/x.iso, file:/path/folder.</summary>
    public static string SourceArgument(Disc.DiscSource source) =>
        source.Kind == Disc.SourceKind.Iso ? $"iso:{source.Path}" : $"file:{source.Path}";

    [GeneratedRegex(@"invalid|not valid|incorrect|rejected|blacklist", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidRegex();

    [GeneratedRegex(@"too old", RegexOptions.IgnoreCase)]
    private static partial Regex TooOldRegex();

    [GeneratedRegex(@"expired", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiredRegex();

    [GeneratedRegex(@"evaluation", RegexOptions.IgnoreCase)]
    private static partial Regex EvaluationRegex();

    [GeneratedRegex(@"MakeMKV v(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"evaluation|too old|expired|registration|registered|key", RegexOptions.IgnoreCase)]
    private static partial Regex KeyWordsRegex();

    [GeneratedRegex(@"^\s*app_Key\s*=\s*""([^""]*)""")]
    private static partial Regex KeyLineRegex();

    [GeneratedRegex(@"^[TM]-[A-Za-z0-9@_]{20,}$")]
    private static partial Regex KeyFormatRegex();

    [GeneratedRegex(@"T-[A-Za-z0-9@_]{40,}")]
    private static partial Regex BetaKeyRegex();
}

public enum KeyStatus { Unknown, Missing, Evaluation, Good, Bad }

public sealed record KeyCheck(KeyStatus Status, string Summary);
