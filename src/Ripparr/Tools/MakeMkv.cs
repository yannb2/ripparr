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
    public static void WriteKey(string home, string? key)
    {
        var path = SettingsPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        lines.RemoveAll(l => KeyLineRegex().IsMatch(l));
        if (!string.IsNullOrWhiteSpace(key))
        {
            lines.Add($"app_Key = \"{key.Trim()}\"");
        }

        File.WriteAllLines(path, lines);
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
