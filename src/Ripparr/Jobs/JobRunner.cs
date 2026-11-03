using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Ripparr.Options;

namespace Ripparr.Jobs;

/// <summary>Takes queued jobs one by one (or a few at a time) and runs them in the background.</summary>
public sealed class JobRunner(
    JobStore store,
    JobEvents events,
    JobPipeline pipeline,
    IOptions<RipparrOptions> options,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _wake = new(0);

    public IReadOnlyCollection<long> Running => _running.Keys.ToList();

    public Job Enqueue(Job job)
    {
        job.Status = JobStatus.Queued;
        job.Created = DateTime.UtcNow;
        store.Add(job);
        events.Write(job.Id, $"Queued: {job.DisplayTitle}, playlist {job.Playlist} of {job.SourceName}{(job.DryRun ? " (dry run)" : "")}.");
        _wake.Release();
        return job;
    }

    /// <summary>Puts a failed, cancelled or interrupted job back in the queue, with the same plan.</summary>
    public void Retry(Job job)
    {
        job.Status = JobStatus.Queued;
        job.Error = null;
        job.Question = null;
        job.Step = null;
        job.Progress = null;
        job.StepStarted = null;
        job.Started = null;
        job.Finished = null;
        job.Checks = [];
        job.Warnings = [];
        store.Update(job);
        events.Write(job.Id, "Queued again.");
        _wake.Release();
    }

    /// <summary>The answer to "unpack instead?": queue the job again, unpacking the ISO with 7-Zip.</summary>
    public void UnpackInstead(Job job)
    {
        job.UnpackIso = true;
        events.Write(job.Id, "Answer: unpack the ISO with 7-Zip.");
        Retry(job);
    }

    public void Cancel(long id)
    {
        if (_running.TryGetValue(id, out var source))
        {
            source.Cancel();
            return;
        }

        var job = store.Get(id);
        if (job is { Status: JobStatus.Queued })
        {
            job.Status = JobStatus.Cancelled;
            job.Finished = DateTime.UtcNow;
            store.Update(job);
            events.Write(id, "Cancelled before it started.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Jobs that were running when the container stopped can't be picked up halfway.
        foreach (var job in store.List(1000, [JobStatus.Running]))
        {
            job.Status = JobStatus.Interrupted;
            job.Finished = DateTime.UtcNow;
            job.Error = "The app stopped while this job was running. Retry it to start again from the beginning.";
            await JobPipeline.UnmountAsync(new JobContext(job, options.Value, store, events, CancellationToken.None));
            store.Update(job);
        }

        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            while (running.Count < options.Value.MaxParallelJobs && store.NextQueued() is { } next)
            {
                running.Add(RunAsync(next, stoppingToken));
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var source in _running.Values)
        {
            await source.CancelAsync();
        }

        await Task.WhenAll(running);
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[job.Id] = source;
        job.Status = JobStatus.Running;
        job.Started = DateTime.UtcNow;
        store.Update(job);
        var context = new JobContext(job, options.Value, store, events, source.Token);

        try
        {
            await Task.Yield();
            await pipeline.RunAsync(context);
            job.Status = JobStatus.Done;
            job.Progress = 1;
            context.Log(job.DryRun ? "Dry run finished." : "Job finished.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.Status = JobStatus.Interrupted;
            job.Error = "The app stopped while this job was running.";
            context.Log(job.Error);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "Cancelled.";
            context.Log("Cancelled. The job folder is kept: " + context.WorkFolder);
        }
        catch (JobQuestionException e)
        {
            job.Status = JobStatus.Waiting;
            job.Error = e.Message;
            job.Question = e.Question;
            context.Log("Waiting for an answer: " + e.Question);
        }
        catch (JobFailedException e)
        {
            job.Status = JobStatus.Failed;
            job.Error = e.Message;
            context.Log("Failed: " + e.Message);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Job {Id} failed", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = $"{e.GetType().Name}: {e.Message}";
            context.Log("Failed: " + job.Error);
        }
        finally
        {
            await JobPipeline.UnmountAsync(context);
            job.Finished = DateTime.UtcNow;
            _running.TryRemove(job.Id, out _);
            store.Update(job);
            events.Raise(job.Id);
            _wake.Release();
        }
    }
}
