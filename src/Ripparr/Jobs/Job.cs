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
    Interrupted,

    /// <summary>Stopped to ask something (Question): the ISO could not be mounted, unpack it instead?</summary>
    Waiting
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

    /// <summary>
    /// Who put out the disc (Criterion, Arrow...), written as the standard Matroska tag DISTRIBUTED_BY.
    /// Not the cut: that is <see cref="Edition"/>. Jellyfin shows it as a "Release: ..." tag (edition_tags.py).
    /// </summary>
    public string? ReleaseLabel { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }

    /// <summary>
    /// For an extra (a featurette, a trailer...): its Jellyfin folder ("featurettes"), from
    /// <see cref="ExtraTypes"/>. Null for the film itself.
    /// </summary>
    public string? ExtraType { get; set; }

    /// <summary>The extra's name: its file name and MKV title ("Making Of").</summary>
    public string? ExtraName { get; set; }

    [JsonIgnore]
    public bool IsExtra => !string.IsNullOrWhiteSpace(ExtraType);

    /// <summary>For a TV episode: the series' TVDB ID. Title and Year are the series'.</summary>
    public int? TvdbId { get; set; }

    /// <summary>For a TV episode: its season (0 = specials) and number in the season.</summary>
    public int? Season { get; set; }

    public int? Episode { get; set; }

    /// <summary>The absolute number, kept for anime series (Sonarr's Anime type puts it in the name).</summary>
    public int? AbsoluteEpisode { get; set; }

    public string? EpisodeTitle { get; set; }

    [JsonIgnore]
    public bool IsEpisode => Season != null && Episode != null;

    /// <summary>"S01E05".</summary>
    [JsonIgnore]
    public string? EpisodeCode => IsEpisode ? EpisodeNumbers.Code(Season!.Value, Episode!.Value) : null;

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

    /// <summary>When the current step started, for the time left.</summary>
    public DateTime? StepStarted { get; set; }

    /// <summary>
    /// How much of the whole job each step is, roughly (set once the job knows how it prepares the
    /// disc): the whole-job percentage adds up the steps done and the share of the current one.
    /// </summary>
    public Dictionary<string, double> StepWeights { get; set; } = [];

    /// <summary>The question a Waiting job asks.</summary>
    public string? Question { get; set; }

    /// <summary>Unpack the ISO with 7-Zip instead of mounting it (the answer to "unpack instead?").</summary>
    public bool UnpackIso { get; set; }

    /// <summary>Where the mount service mounted the ISO for this job; unmounted when the job ends.</summary>
    public string? MountedAt { get; set; }

    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public List<VerifyCheck> Checks { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    /// <summary>The tracks as they went into the MKV (filled when the MKV is built).</summary>
    public List<WrittenTrack> Written { get; set; } = [];

    /// <summary>The subtitle tracks kept for the preview (SubtitleFolder/job-12/).</summary>
    public List<JobSubtitle> Subtitles { get; set; } = [];

    /// <summary>Taken off the queue page with Remove (a failed, interrupted or waiting job): it shows in History only.</summary>
    public bool Dismissed { get; set; }

    /// <summary>
    /// On the queue page: waiting to run, running, or needing a look (failed, interrupted, waiting for
    /// an answer) until removed. Done and cancelled jobs go to History.
    /// </summary>
    [JsonIgnore]
    public bool InQueue => Status is JobStatus.Queued or JobStatus.Running
                           || !Dismissed && Status is JobStatus.Waiting or JobStatus.Failed or JobStatus.Interrupted;

    [JsonIgnore]
    public bool IsFinished => Status is JobStatus.Done or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted or JobStatus.Waiting;

    [JsonIgnore]
    public bool CanRetry => Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted or JobStatus.Waiting;

    /// <summary>The whole job, 0 to 1: steps done plus the current step's share. Null before the weights are known.</summary>
    [JsonIgnore]
    public double? Overall => JobProgress.Overall(StepWeights, Step, Progress);

    /// <summary>Time left in the current step, from how fast it went so far. Null until there is enough to go on.</summary>
    [JsonIgnore]
    public TimeSpan? StepTimeLeft => JobProgress.TimeLeft(StepStarted, Progress, DateTime.UtcNow);

    [JsonIgnore]
    public string DisplayTitle => (string.IsNullOrWhiteSpace(Title)
        ? SourceName
        : Year is { } y ? $"{Title} ({y}){(string.IsNullOrWhiteSpace(Edition) ? "" : " - " + Edition)}" : Title)
        + (IsExtra ? $" · {ExtraName}" : "")
        + (IsEpisode ? $" · {EpisodeCode}{(string.IsNullOrWhiteSpace(EpisodeTitle) ? "" : " - " + EpisodeTitle)}" : "");

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
            ReleaseLabel = copy.ReleaseLabel,
            TmdbId = copy.TmdbId,
            ImdbId = copy.ImdbId,
            OriginalLanguage = copy.OriginalLanguage,
            Profile = copy.Profile,
            Plan = copy.Plan,
            Tags = copy.Tags,
            ExtraType = copy.ExtraType,
            ExtraName = copy.ExtraName,
            TvdbId = copy.TvdbId,
            Season = copy.Season,
            Episode = copy.Episode,
            AbsoluteEpisode = copy.AbsoluteEpisode,
            EpisodeTitle = copy.EpisodeTitle
        };
    }

    [JsonIgnore]
    public TimeSpan? Elapsed => Started is { } s ? (Finished ?? DateTime.UtcNow) - s : null;
}

/// <summary>
/// The kinds of extras, by the folder they go in next to the film: names both Jellyfin (which
/// shows them as extras) and Radarr (which leaves them alone) know.
/// </summary>
public static class ExtraTypes
{
    public static readonly (string Folder, string Label, string Singular)[] All =
    [
        ("featurettes", "Featurettes", "Featurette"),
        ("behind the scenes", "Behind the scenes", "Behind the Scenes"),
        ("deleted scenes", "Deleted scenes", "Deleted Scene"),
        ("interviews", "Interviews", "Interview"),
        ("scenes", "Scenes", "Scene"),
        ("shorts", "Shorts", "Short"),
        ("trailers", "Trailers", "Trailer"),
        ("extras", "Other extras", "Extra")
    ];

    public static bool Known(string? folder) => All.Any(t => t.Folder == folder);

    public static string Label(string? folder) => All.FirstOrDefault(t => t.Folder == folder).Label ?? folder ?? "";

    /// <summary>"Featurette 2": the name an extra gets when none is typed.</summary>
    public static string DefaultName(string folder, int number) =>
        $"{All.FirstOrDefault(t => t.Folder == folder).Singular ?? "Extra"} {number}";
}

/// <summary>The whole-job percentage and the time left.</summary>
public static class JobProgress
{
    /// <summary>
    /// Rough share of each step in a UHD remux: copying or decrypting the disc, DGDemux and mkvmerge
    /// move all the data, dovi_tool the video only, the OCR depends on how many SRT tracks.
    /// </summary>
    public static Dictionary<string, double> Weights(JobPipeline.Prepare prepare, bool mergeDolbyVision, int srtTracks) => new()
    {
        [JobSteps.Check] = 1,
        [JobSteps.Prepare] = prepare switch
        {
            JobPipeline.Prepare.Decrypt => 30,
            JobPipeline.Prepare.Unpack => 20,
            _ => 0.5
        },
        [JobSteps.Demux] = 30,
        [JobSteps.ReadStreams] = 2,
        [JobSteps.Subtitles] = 2 + 6 * srtTracks,
        [JobSteps.DolbyVision] = mergeDolbyVision ? 15 : 0,
        [JobSteps.Mux] = 25,
        [JobSteps.Verify] = 3,
        [JobSteps.Finish] = 1
    };

    public static double? Overall(IReadOnlyDictionary<string, double> weights, string? step, double? progress)
    {
        var total = weights.Values.Sum();
        if (total <= 0 || step == null || !JobSteps.All.Contains(step))
        {
            return null;
        }

        var done = 0.0;
        foreach (var name in JobSteps.All)
        {
            var weight = weights.GetValueOrDefault(name);
            if (name == step)
            {
                done += weight * Math.Clamp(progress ?? 0, 0, 1);
                break;
            }

            done += weight;
        }

        return Math.Clamp(done / total, 0, 1);
    }

    public static TimeSpan? TimeLeft(DateTime? started, double? progress, DateTime now)
    {
        if (started is not { } s || progress is not { } p || p < 0.01 || p >= 1)
        {
            return null;
        }

        var elapsed = now - s;
        return elapsed < TimeSpan.FromSeconds(10) ? null : TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p) / p);
    }
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

/// <summary>Episode numbers as Sonarr writes them.</summary>
public static class EpisodeNumbers
{
    public static string Code(int season, int episode) =>
        $"S{season.ToString("00", System.Globalization.CultureInfo.InvariantCulture)}E{episode.ToString("00", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>"005", as Sonarr's {absolute:000}.</summary>
    public static string Absolute(int absolute) => absolute.ToString("000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Sonarr's season folder: "Season 01", and "Specials" for season 0.</summary>
    public static string SeasonFolder(int season) =>
        season == 0 ? "Specials" : $"Season {season.ToString("00", System.Globalization.CultureInfo.InvariantCulture)}";
}
