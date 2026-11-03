using System.Text;
using System.Text.RegularExpressions;

namespace Ripparr.Subtitles;

/// <summary>
/// Removes the text written for the deaf and hard of hearing, the way Subtitle Edit's
/// "Remove text for HI" does: sound descriptions in [brackets] and (parentheses), speaker
/// labels ("JOHN:", "MAN #2:"), lyrics between music notes, and the lines and dialogue
/// dashes left empty by that.
/// </summary>
public static partial class SdhCleaner
{
    public static string Clean(string text)
    {
        // Brackets can span lines: remove them on the whole caption first.
        var cleaned = Brackets().Replace(text, "");
        cleaned = Parentheses().Replace(cleaned, "");

        var lines = new List<string>();
        foreach (var raw in cleaned.Split('\n'))
        {
            var line = raw.Trim();
            if (MusicLine().IsMatch(line))
            {
                continue;
            }

            line = SpeakerLabel().Replace(line, "$1");
            line = line.Replace("♪", "").Replace("♫", "");
            line = Spaces().Replace(line, " ").Trim();
            if (line.Length == 0 || DashOnly().IsMatch(line) || PunctuationOnly().IsMatch(line))
            {
                continue;
            }

            lines.Add(line);
        }

        // A dialogue dash only makes sense with two speakers.
        if (lines.Count == 1)
        {
            lines[0] = LeadingDash().Replace(lines[0], "");
        }

        return string.Join('\n', lines);
    }

    /// <summary>True when the cleaner would change the caption.</summary>
    public static bool HasSdh(string text) => Clean(text) != Normalise(text);

    private static string Normalise(string text) =>
        string.Join('\n', text.Split('\n').Select(l => Spaces().Replace(l.Trim(), " ")).Where(l => l.Length > 0));

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Singleline)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.Singleline)]
    private static partial Regex Parentheses();

    // A line that is a lyric: starts or ends with a music note (or a # OCR'd from one).
    [GeneratedRegex(@"^-?\s*[♪♫#].*|.*[♪♫]$")]
    private static partial Regex MusicLine();

    // "JOHN:", "MAN #2:", "DR. SMITH:" at the start of a line, after an optional dialogue dash.
    [GeneratedRegex(@"^(-\s*)?[A-ZÀ-Ý][A-ZÀ-Ý0-9 .'#&-]{0,30}:\s+")]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^-+$")]
    private static partial Regex DashOnly();

    [GeneratedRegex(@"^[\s.,:;!?…-]+$")]
    private static partial Regex PunctuationOnly();

    [GeneratedRegex(@"^-\s*")]
    private static partial Regex LeadingDash();
}

/// <summary>One cue of an SRT file.</summary>
public sealed record SrtCue(TimeSpan Start, TimeSpan End, string Text);

public static class Srt
{
    public static string Write(IEnumerable<SrtCue> cues)
    {
        var text = new StringBuilder();
        var n = 0;
        foreach (var cue in cues.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
        {
            text.Append(++n).Append('\n')
                .Append(Time(cue.Start)).Append(" --> ").Append(Time(cue.End)).Append('\n')
                .Append(cue.Text.Trim()).Append("\n\n");
        }

        return text.ToString();
    }

    public static string Time(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";
}
