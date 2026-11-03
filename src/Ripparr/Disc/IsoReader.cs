using Ripparr.Tools;

namespace Ripparr.Disc;

public sealed record IsoEntry(string Path, long Size, bool Folder);

/// <summary>Reads a Blu-ray ISO with 7-Zip (UDF up to 2.60), so no loop mount and no extra rights are needed.</summary>
public sealed class IsoReader(string sevenZip)
{
    public async Task<(List<IsoEntry>? Entries, string? Error)> ListAsync(string iso, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(sevenZip, ["l", "-slt", "-ba", iso], TimeSpan.FromMinutes(2), cancellationToken);
        if (!result.Success)
        {
            return (null, result.NotStarted ? "7-Zip (7z) is not installed." : $"7-Zip could not read the ISO: {result.LastLine}");
        }

        return (ParseListing(result.Output), null);
    }

    /// <summary>Unpacks the given paths (7-Zip wildcards such as BDMV/PLAYLIST/*) into a folder.</summary>
    public async Task<ProcessResult> ExtractAsync(string iso, string folder, IEnumerable<string> patterns,
        CancellationToken cancellationToken, Action<string>? onLine = null, TimeSpan? timeout = null)
    {
        List<string> arguments = ["x", iso, $"-o{folder}", "-y", "-bsp1"];
        arguments.AddRange(patterns);
        return await ProcessRunner.RunAsync(new ProcessSpec(sevenZip, arguments), timeout ?? TimeSpan.FromMinutes(5),
            onLine, cancellationToken);
    }

    /// <summary>Reads <c>7z l -slt -ba</c>: blocks of "Key = value" lines, one block per entry.</summary>
    internal static List<IsoEntry> ParseListing(string output)
    {
        var entries = new List<IsoEntry>();
        string? path = null;
        long size = 0;
        var folder = false;

        void Flush()
        {
            if (path != null)
            {
                entries.Add(new IsoEntry(path.Replace('\\', '/'), size, folder));
            }

            path = null;
            size = 0;
            folder = false;
        }

        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var eq = raw.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0)
            {
                if (raw.Trim().Length == 0)
                {
                    Flush();
                }

                continue;
            }

            var key = raw[..eq].Trim();
            var value = raw[(eq + 3)..].Trim();
            switch (key)
            {
                case "Path":
                    Flush();
                    path = value;
                    break;
                case "Size":
                    long.TryParse(value, out size);
                    break;
                case "Folder":
                    folder = value == "+";
                    break;
            }
        }

        Flush();
        return entries;
    }
}
