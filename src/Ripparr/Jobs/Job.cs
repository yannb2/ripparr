using System.Text.Json.Serialization;
using Ripparr.Disc;
using Ripparr.Profiles;

namespace Ripparr.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,

    /// <summary>Was running when the container stopped.</summary>
    Interrupted
}

/// <summary>One track of the finished MKV, for the job page. Jobs saved before 0.1.3 have no details, size or bit rate.</summary>
public sealed record WrittenTrack(string Kind, string Codec, string Language, string Name, string Flags,
    string Details = "", long Size = 0, long BitRate = 0, long BitRateMaximum = 0, string File = "");

/// <summary>
/// A subtitle track kept for the preview: Key.sup (the pictures), and with OCR Key.ocr.json
/// (what Tesseract read, before and after the SDH removal) and Key.srt (what went into the MKV).
/// </summary>
public sealed record JobSubtitle(string Key, string Language, string Name, bool Forced, int Captions,
    bool Ocr = false, string? SrtName = null, string? OcrSummary = null);

/// <summary>One check of the verify step.</summary>
public sealed record VerifyCheck(string Name, bool Passed, string Detail, bool Warning = false);

/// <summary>A remux job: one playlist of one source into one MKV. Stored as JSON.</summary>
public sealed class Job
{
    public long Id { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    public string SourcePath { get; set; } = "";
    public SourceKind SourceKind { get; set; }
    public string SourceName { get; set; } = "";
    public string Playlist { get; set; } = "";

    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Edition { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }

    /// <summary>ISO 639-2 code of the film's language ("eng").</summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>The profile as it was when the job was made: editing the profile later does not change the job.</summary>
    public RipProfile Profile { get; set; } = new();

    /// <summary>The job's own global tags (RELEASE_GROUP...): the profile's, filled in on the job form.</summary>
    public List<GlobalTag> Tags { get; set; } = [];

    public TrackPlan Plan { get; set; } = new();

    /// <summary>Print the commands without running the long steps.</summary>
    public bool DryRun { get; set; }

    /// <summary>The step running now, or the one that failed.</summary>
    public string? Step { get; set; }

    /// <summary>Progress of the current step, 0 to 1. Null when the tool gives none.</summary>
    public double? Progress { get; set; }

    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public List<VerifyCheck> Checks { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    /// <summary>The tracks as they went into the MKV (filled when the MKV is built).</summary>
    public List<WrittenTrack> Written { get; set; } = [];

    /// <summary>The subtitle tracks kept for the preview (SubtitleFolder/job-12/).</summary>
    public List<JobSubtitle> Subtitles { get; set; } = [];

    [JsonIgnore]
    public bool IsFinished => Status is JobStatus.Done or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted;

    [JsonIgnore]
    public bool CanRetry => Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? SourceName
        : Year is { } y ? $"{Title} ({y}){(string.IsNullOrWhiteSpace(Edition) ? "" : " - " + Edition)}" : Title;

    /// <summary>
    /// A new job with this one's source, playlist, title, profile, track plan and tags, not a dry
    /// run, and nothing of this run: for "Queue the job" after a dry run.
    /// </summary>
    public Job ForReal()
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<Job>(System.Text.Json.JsonSerializer.Serialize(this, JobStore.Json), JobStore.Json)!;
        return new Job
        {
            SourcePath = copy.SourcePath,
            SourceKind = copy.SourceKind,
            SourceName = copy.SourceName,
            Playlist = copy.Playlist,
            Title = copy.Title,
            Year = copy.Year,
            Edition = copy.Edition,
            TmdbId = copy.TmdbId,
            ImdbId = copy.ImdbId,
            OriginalLanguage = copy.OriginalLanguage,
            Profile = copy.Profile,
            Plan = copy.Plan,
            Tags = copy.Tags
        };
    }

    [JsonIgnore]
    public TimeSpan? Elapsed => Started is { } s ? (Finished ?? DateTime.UtcNow) - s : null;
}

/// <summary>The steps of a job, in order, for the progress bar.</summary>
public static class JobSteps
{
    public const string Check = "Check";
    public const string Prepare = "Prepare";
    public const string Demux = "Demux";
    public const string ReadStreams = "Read streams";
    public const string Subtitles = "Subtitles";
    public const string DolbyVision = "Dolby Vision";
    public const string Mux = "Build the MKV";
    public const string Verify = "Verify";
    public const string Finish = "Finish";

    public static readonly string[] All = [Check, Prepare, Demux, ReadStreams, Subtitles, DolbyVision, Mux, Verify, Finish];
}
