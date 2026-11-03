namespace Ripparr.Options;

/// <summary>A folder Ripparr has to write to, and why it cannot.</summary>
public sealed record FolderProblem(string Name, string Path, string Error);

/// <summary>
/// Checks that the folders Ripparr writes to exist (or can be made) and take a new file.
/// A folder made with sudo belongs to root, and the container runs as another user.
/// </summary>
public static class FolderCheck
{
    public static List<FolderProblem> Problems(RipparrOptions options)
    {
        var folders = new (string Name, string Path)[]
        {
            ("Work folder", options.WorkFolder),
            ("Done folder", options.DoneFolder),
            ("Config folder", System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.DatabasePath)) ?? "/config"),
            ("Logs", options.LogFolder),
            ("MakeMKV settings", options.MakeMkvHome)
        };
        var problems = new List<FolderProblem>();
        foreach (var (name, path) in folders)
        {
            if (Check(path) is { } error)
            {
                problems.Add(new FolderProblem(name, path, error));
            }
        }

        return problems;
    }

    /// <summary>Null when a file can be written in the folder, otherwise the reason.</summary>
    public static string? Check(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = System.IO.Path.Combine(path, $".ripparr-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return e is UnauthorizedAccessException ? "not writable" : e.Message;
        }
    }

    /// <summary>The user and group the app runs as ("1000:1000"), for the chown hint.</summary>
    public static string CurrentUser()
    {
        try
        {
            string? Id(string key) => File.ReadLines("/proc/self/status")
                .FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))?
                .Split('\t', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            return Id("Uid:") is { } uid ? $"{uid}:{Id("Gid:") ?? uid}" : "1000:1000";
        }
        catch (IOException)
        {
            return "1000:1000";
        }
    }

    /// <summary>One line to tell the user how to fix it.</summary>
    public static string Hint(IEnumerable<FolderProblem> problems) =>
        $"sudo chown -R {CurrentUser()} " + string.Join(' ', problems.Select(p => p.Path).Distinct());
}
