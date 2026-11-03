using Ripparr.Tools;

namespace Ripparr.Jobs;

/// <summary>What the Homepage tile shows.</summary>
public sealed record Stats(int Queued, int Running, string? Current, double? Progress, int DoneToday, int Failed, int Updates)
{
    public static Stats From(JobStore store, ToolStatusService tools)
    {
        var jobs = store.List(500);
        var running = jobs.FirstOrDefault(j => j.Status == JobStatus.Running);
        var today = DateTime.UtcNow.Date;
        return new Stats(
            jobs.Count(j => j.Status == JobStatus.Queued),
            jobs.Count(j => j.Status == JobStatus.Running),
            running == null ? null : $"{running.DisplayTitle}: {running.Step}",
            running?.Progress is { } p ? Math.Round(p * 100, 1) : null,
            jobs.Count(j => j.Status == JobStatus.Done && j.Finished >= today && !j.DryRun),
            jobs.Count(j => j.Status is JobStatus.Failed or JobStatus.Interrupted && j.Finished >= today.AddDays(-7)),
            tools.Statuses.Count(s => s.UpdateAvailable));
    }
}
