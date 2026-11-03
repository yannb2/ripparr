using System.Globalization;
using System.Text.RegularExpressions;
using Ripparr.Tools;

namespace Ripparr.Media;

/// <summary>What dovi_tool reads from the Dolby Vision RPU (the per-frame metadata) of a file.</summary>
public sealed class DolbyVisionDetails
{
    /// <summary>How many frames dovi_tool read.</summary>
    public int Frames { get; init; }

    /// <summary>True when every frame was read, false for the sample at the start.</summary>
    public bool WholeFilm { get; init; }

    /// <summary>"5", "7", "8"... as the RPU says.</summary>
    public string? Profile { get; init; }

    /// <summary>"FEL" or "MEL" for profile 7, null otherwise.</summary>
    public string? Layer { get; init; }

    /// <summary>"CM v2.9" or "CM v4.0".</summary>
    public string? CmVersion { get; init; }

    /// <summary>Every line of dovi_tool's summary, in its own words: ("L6 metadata", "MaxCLL: 1000 nits...").</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Lines { get; init; } = [];

    public string? Get(string label)
    {
        return Lines.FirstOrDefault(l => string.Equals(l.Key, label, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

/// <param name="RawSummary">dovi_tool's summary text, kept so it can be read again from the cache.</param>
public sealed record DolbyVisionResult(DolbyVisionDetails? Details, string? Error, string? RawSummary = null);

/// <summary>
/// Runs quietvoid's dovi_tool: extract-rpu on the file, then info --summary on
/// the RPU. ffprobe gives the profile; FEL or MEL, the CM version, L1 to L11
/// metadata are only in the RPU.
/// </summary>
public static partial class DoviTool
{
    public static string Executable { get; set; } = "dovi_tool";

    /// <summary>About 20 seconds of film: enough for the profile, layer and CM version, read in a second or two.</summary>
    public const int SampleFrames = 500;

    public static async Task<DolbyVisionResult> ReadAsync(string path, int? frames, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var rpu = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}.rpu");
        var link = HasMatroskaExtension(path) ? null : LinkAsMatroska(path);
        try
        {
            List<string> extract = ["extract-rpu"];
            if (frames is { } limit)
            {
                extract.AddRange(["--limit", limit.ToString(CultureInfo.InvariantCulture)]);
            }

            extract.AddRange(["-o", rpu, link ?? path]);
            var result = await ProcessRunner.RunAsync(Executable, extract, timeout, cancellationToken);
            if (!result.Success || !File.Exists(rpu) || new FileInfo(rpu).Length == 0)
            {
                return new DolbyVisionResult(null, Failure("could not read the RPU", result));
            }

            var info = await ProcessRunner.RunAsync(Executable, ["info", "--summary", "-i", rpu],
                TimeSpan.FromMinutes(2), cancellationToken);
            if (!info.Success)
            {
                return new DolbyVisionResult(null, Failure("could not read the RPU summary", info));
            }

            var details = ParseSummary(info.Output, wholeFilm: frames == null);
            return details == null
                ? new DolbyVisionResult(null, "dovi_tool printed no summary.")
                : new DolbyVisionResult(details, null, info.Output);
        }
        finally
        {
            TryDelete(rpu);
            if (link != null)
            {
                TryDelete(link);
            }
        }
    }

    /// <summary>
    /// Reads the "Summary:" block of dovi_tool info --summary. Each line is
    /// "Label: value"; a line without a label belongs to the one before.
    /// </summary>
    public static DolbyVisionDetails? ParseSummary(string? output, bool wholeFilm)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim().Equals("Summary:", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return null;
        }

        var entries = new List<KeyValuePair<string, string>>();
        foreach (var raw in lines.Skip(start + 1))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && raw.StartsWith("  ", StringComparison.Ordinal) && !raw.StartsWith("    ", StringComparison.Ordinal))
            {
                entries.Add(new(line[..colon].Trim(), line[(colon + 2)..].Trim()));
            }
            else if (entries.Count > 0)
            {
                var last = entries[^1];
                entries[^1] = new(last.Key, $"{last.Value}\n{line}");
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var profileLine = entries.FirstOrDefault(e => e.Key.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)).Value;
        var dmLine = entries.FirstOrDefault(e => e.Key.Equals("DM version", StringComparison.OrdinalIgnoreCase)).Value;
        var framesLine = entries.FirstOrDefault(e => e.Key.Equals("Frames", StringComparison.OrdinalIgnoreCase)).Value;

        return new DolbyVisionDetails
        {
            Frames = int.TryParse(framesLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0,
            WholeFilm = wholeFilm,
            Profile = profileLine is null ? null : ProfileRegex().Match(profileLine) is { Success: true } p ? p.Value : null,
            Layer = Layer(profileLine),
            CmVersion = dmLine is null ? null : CmRegex().Match(dmLine) is { Success: true } cm ? $"CM v{cm.Groups[1].Value}" : null,
            Lines = entries
        };
    }

    /// <summary>"7 (FEL)" -> FEL. A sample with both ("7 (FEL, MEL)") counts as FEL.</summary>
    private static string? Layer(string? profileLine)
    {
        if (profileLine == null)
        {
            return null;
        }

        if (profileLine.Contains("FEL", StringComparison.OrdinalIgnoreCase))
        {
            return "FEL";
        }

        return profileLine.Contains("MEL", StringComparison.OrdinalIgnoreCase) ? "MEL" : null;
    }

    // dovi_tool picks its reader from the file extension and reads anything not
    // named .mkv as raw HEVC, which finds no RPU in a Matroska file.
    public static bool HasMatroskaExtension(string path)
    {
        return string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A *.mkv symbolic link in the temp folder, or null when one can't be made.</summary>
    public static string? LinkAsMatroska(string path)
    {
        var link = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}.mkv");
        try
        {
            File.CreateSymbolicLink(link, Path.GetFullPath(path));
            return link;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Failure(string what, ProcessResult result)
    {
        if (result.ExitCode == -1 && result.Error.StartsWith("Could not start", StringComparison.Ordinal))
        {
            return "dovi_tool is not installed, so the Dolby Vision details can't be read.";
        }

        var detail = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
        var last = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(last) ? $"dovi_tool {what}." : $"dovi_tool {what}: {last}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^\d+(\.\d+)?")]
    private static partial Regex ProfileRegex();

    [GeneratedRegex(@"CM v(\d+(\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CmRegex();
}
