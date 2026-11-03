using Ripparr.Options;
using Ripparr.Tools;

namespace Ripparr.Jobs;

/// <summary>What a running job's steps share: the job, where it works, and how it reports.</summary>
public sealed class JobContext(Job job, RipparrOptions options, JobStore store, JobEvents events, CancellationToken cancellationToken)
{
    private DateTime _lastSave = DateTime.MinValue;
    private double? _lastProgress;

    public Job Job { get; } = job;
    public RipparrOptions Options { get; } = options;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public string WorkFolder => Path.Combine(Options.WorkFolder, $"job-{Job.Id}");
    public string DiscFolder => Path.Combine(WorkFolder, "disc");
    public string DemuxFolder => Path.Combine(WorkFolder, "demux");
    public string OutFolder => Path.Combine(WorkFolder, "out");

    public void Log(string line) => events.Write(Job.Id, line);

    public void Warn(string line)
    {
        Job.Warnings.Add(line);
        Log("Warning: " + line);
    }

    public void Step(string step)
    {
        Job.Step = step;
        Job.Progress = null;
        _lastProgress = null;
        Log($"== {step}");
        Save();
    }

    /// <summary>Updates the progress bar; written to the database at most every few seconds.</summary>
    public void Progress(double? value)
    {
        if (value is { } v && _lastProgress is { } last && Math.Abs(v - last) < 0.005)
        {
            return;
        }

        _lastProgress = value;
        Job.Progress = value;
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(5))
        {
            Save();
        }
        else
        {
            events.Raise(Job.Id);
        }
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        store.Update(Job);
        events.Raise(Job.Id);
    }

    /// <summary>Runs a tool, logs its command and output, and fails the step when it fails.</summary>
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, Func<string, bool>? onLine = null,
        bool throwOnError = true)
    {
        Log("$ " + spec.CommandLine);
        var result = await ProcessRunner.RunAsync(spec, timeout, line =>
        {
            // The handler says whether the line was only progress (not worth logging).
            if (onLine?.Invoke(line) != true)
            {
                Log("  " + line);
            }
        }, CancellationToken);

        if (!result.Success && throwOnError)
        {
            var reason = result.NotStarted
                ? $"{Path.GetFileName(spec.FileName)} is not installed."
                : $"{Path.GetFileName(spec.FileName)} failed (exit code {result.ExitCode}): {result.LastLine}";
            throw new JobFailedException(reason);
        }

        return result;
    }
}

/// <summary>A step failed for a reason worth showing as it is.</summary>
public sealed class JobFailedException(string message) : Exception(message);
