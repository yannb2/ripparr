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
    public int? Tmdb { get; set; }
    public string? Imdb { get; set; }
    public string? Original { get; set; }
    public Dictionary<string, PlaylistDraft> Playlists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ExtrasPickerState Extras { get; } = new();
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
    }

    public Dictionary<string, Row> Rows { get; } = new();
    public bool Open { get; set; }
    public bool ShowAll { get; set; }
    public bool InSeason { get; set; }
    public string ProfileId { get; set; } = "";
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

    /// <summary>"Start over": the disc's page comes back as on a first visit.</summary>
    public DiscDraft Reset(string sourcePath)
    {
        _drafts.TryRemove(sourcePath, out _);
        return Get(sourcePath);
    }
}
