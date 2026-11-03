using System.Collections.Concurrent;

namespace Ripparr.Jobs;

/// <summary>
/// The live side of running jobs: the latest log lines in memory, and an event the pages
/// listen to so the progress bar and the log move without a refresh.
/// </summary>
public sealed class JobEvents(Microsoft.Extensions.Options.IOptions<Options.RipparrOptions> options)
{
    private const int KeptLines = 2000;
    private readonly ConcurrentDictionary<long, List<string>> _lines = new();

    /// <summary>A job changed (status, step, progress) or wrote a line. The argument is the job id.</summary>
    public event Action<long>? Changed;

    public string LogPath(long id) => Path.Combine(options.Value.LogFolder, $"job-{id}.log");

    public void Write(long id, string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        var lines = _lines.GetOrAdd(id, _ => []);
        lock (lines)
        {
            lines.Add(stamped);
            if (lines.Count > KeptLines)
            {
                lines.RemoveRange(0, lines.Count - KeptLines);
            }
        }

        try
        {
            Directory.CreateDirectory(options.Value.LogFolder);
            File.AppendAllText(LogPath(id), stamped + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log on the page is enough when /config can't be written.
        }

        Raise(id);
    }

    /// <summary>The last lines: from memory while the app runs, from the log file after a restart.</summary>
    public List<string> Lines(long id, int last = 500)
    {
        if (_lines.TryGetValue(id, out var lines))
        {
            lock (lines)
            {
                return lines.TakeLast(last).ToList();
            }
        }

        try
        {
            var path = LogPath(id);
            return File.Exists(path) ? File.ReadLines(path).TakeLast(last).ToList() : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public void Raise(long id)
    {
        try
        {
            Changed?.Invoke(id);
        }
        catch (Exception)
        {
            // A page that went away must not break the job.
        }
    }
}
