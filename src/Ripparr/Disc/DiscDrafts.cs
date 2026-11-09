using System.Collections.Concurrent;
using Ripparr.Profiles;

namespace Ripparr.Disc;

/// <summary>
/// What was typed and ticked on a disc's page, kept while the app runs so that leaving the page
/// (a subtitle preview, the job page after a dry run, another menu) and coming back finds it as
/// it was. One draft per disc; each playlist keeps its own track plan, profile and tags.
/// </summary>
public sealed class DiscDraft
{
    public string? Playlist { get; set; }
    public bool? Tv { get; set; }
    public bool ShowAll { get; set; }
    public string Search { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Edition { get; set; }
    public string? Label { get; set; }
    public string? SourceGroup { get; set; }
    public string? Country { get; set; }
    public int? Tmdb { get; set; }
    public string? Imdb { get; set; }
    public string? Original { get; set; }
    public Dictionary<string, PlaylistDraft> Playlists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ExtrasPickerState Extras { get; } = new();

    /// <summary>The TV episodes card, with its own extras card.</summary>
    public EpisodesPickerState Episodes { get; } = new();

    /// <summary>The disc's name, for the sidebar's list of discs being worked on.</summary>
    public string Name { get; set; } = "";

    public DateTime Touched { get; set; } = DateTime.UtcNow;

    /// <summary>True once the page has saved into it: there is something to put back.</summary>
    public bool Saved => Playlist != null;
}

/// <summary>A playlist's job form: the track plan with its edits, the profile, the tags.</summary>
public sealed class PlaylistDraft
{
    public required TrackPlan Plan { get; init; }
    public required string ProfileId { get; init; }
    public required List<GlobalTag> Tags { get; init; }
    public required HashSet<int> Compare { get; init; }

    /// <summary>The subtitle read's guesses already put on the plan, so they don't come back over an untick.</summary>
    public (TrackPlan? Plan, int Version) Flagged { get; init; }

    /// <summary>Chapter names typed in the Chapters card, by chapter number.</summary>
    public Dictionary<int, string> ChapterNames { get; init; } = [];

    /// <summary>SDH and commentary as the app last set them: a flag changed by hand is not set again by a read.</summary>
    public Dictionary<int, (bool Sdh, bool Commentary)> Auto { get; init; } = [];
}

/// <summary>The extras card: rows picked, names typed, the profile.</summary>
public sealed class ExtrasPickerState
{
    public sealed class Row
    {
        public bool Selected { get; set; }
        public string Type { get; set; } = "featurettes";
        public string Name { get; set; } = "";
        public bool Pictures { get; set; }
        public bool Watch { get; set; }
        public bool Tracks { get; set; }

        /// <summary>The tracks, once opened: kept as edited until Reset.</summary>
        public TrackPlan? Plan { get; set; }

        public PlaylistTracksState Subtitles { get; set; } = new();
    }

    public Dictionary<string, Row> Rows { get; } = new();

    /// <summary>Reels shown as their pieces, one extra each, instead of one extra for the whole reel.</summary>
    public HashSet<string> Split { get; } = new(StringComparer.Ordinal);

    public bool Open { get; set; }
    public bool ShowAll { get; set; }
    public bool InSeason { get; set; }
    public string ProfileId { get; set; } = "";
}

/// <summary>The TV episodes card: the series, numbers, titles and each episode's tracks.</summary>
public sealed class EpisodesPickerState
{
    /// <summary>The card has filled itself in once (guesses, ticks): coming back keeps what is here.</summary>
    public bool Started { get; set; }

    public Dictionary<string, EpisodeRow> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowAll { get; set; }
    public string ProfileId { get; set; } = "";
    public string Search { get; set; } = "";
    public bool LookedUp { get; set; }
    public string? Picked { get; set; }
    public List<Arr.SeriesMatch> Matches { get; set; } = [];
    public List<Arr.EpisodeInfo> Known { get; set; } = [];
    public string? EpisodesNote { get; set; }
    public string? ResumeNote { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public int? Tvdb { get; set; }
    public int? Tmdb { get; set; }
    public string? Imdb { get; set; }
    public string? Original { get; set; }

    /// <summary>Where the disc came from: the ISO's group, or "Own disc" (Job.OwnDisc).</summary>
    public string? SourceGroup { get; set; }

    /// <summary>The disc's country (GBR...), usually empty.</summary>
    public string? Country { get; set; }

    public bool Anime { get; set; }
    public int Season { get; set; } = 1;
    public int First { get; set; } = 1;
    public ExtrasPickerState Extras { get; } = new();
}

/// <summary>One playlist on the TV episodes card.</summary>
public sealed class EpisodeRow
{
    public bool Selected { get; set; }
    public int? Number { get; set; }
    public string Title { get; set; } = "";
    public bool Pictures { get; set; }
    public bool Watch { get; set; }
    public bool Tracks { get; set; }

    /// <summary>A DVD title: the "split by chapters" panel is open.</summary>
    public bool Split { get; set; }

    /// <summary>How many episodes the title holds, as typed in the split panel.</summary>
    public int? SplitEpisodes { get; set; }

    /// <summary>The chapters the episodes start at, as typed ("1, 7, 13").</summary>
    public string SplitStarts { get; set; } = "";

    /// <summary>The tracks, once opened: kept as edited (Reset goes back to the profile).</summary>
    public TrackPlan? Plan { get; set; }

    public PlaylistTracksState Subtitles { get; } = new();
}

/// <summary>The drafts, in memory: they last until the app restarts, or two weeks unused.</summary>
public sealed class DiscDrafts
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
    private readonly ConcurrentDictionary<string, DiscDraft> _drafts = new(StringComparer.Ordinal);

    public DiscDraft Get(string sourcePath)
    {
        foreach (var (key, old) in _drafts)
        {
            if (DateTime.UtcNow - old.Touched > KeepFor)
            {
                _drafts.TryRemove(key, out _);
            }
        }

        var draft = _drafts.GetOrAdd(sourcePath, _ => new DiscDraft());
        draft.Touched = DateTime.UtcNow;
        return draft;
    }

    /// <summary>The discs being worked on (something typed or ticked), newest first: the sidebar lists them.</summary>
    public List<(string Path, string Name)> Recent(int count = 6) => _drafts
        .Where(d => (d.Value.Saved || d.Value.Episodes.Started) && d.Value.Name.Length > 0 && DateTime.UtcNow - d.Value.Touched <= KeepFor)
        .OrderByDescending(d => d.Value.Touched)
        .Take(count)
        .Select(d => (d.Key, d.Value.Name))
        .ToList();

    /// <summary>Takes a disc off the sidebar list (and forgets what was typed on it).</summary>
    public void Forget(string sourcePath) => _drafts.TryRemove(sourcePath, out _);

    /// <summary>"Start over": the disc's page comes back as on a first visit.</summary>
    public DiscDraft Reset(string sourcePath)
    {
        _drafts.TryRemove(sourcePath, out _);
        return Get(sourcePath);
    }
}

/// <summary>
/// An episode's or extra's subtitle read as it went onto its plan: the tracks to compare, the read
/// whose flags are on, and the flags as the app last set them (a difference is your choice).
/// </summary>
public sealed class PlaylistTracksState
{
    public HashSet<int> Compare { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public TrackPlan? Plan { get; set; }

    public int FlaggedVersion { get; set; } = -1;

    public Dictionary<int, (bool Sdh, bool Commentary)> Auto { get; set; } = [];
}
