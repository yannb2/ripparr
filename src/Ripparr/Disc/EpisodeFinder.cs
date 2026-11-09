namespace Ripparr.Disc;

/// <summary>Where a TV disc's episode order comes from, the disc's own word first.</summary>
public enum DiscOrderSource
{
    /// <summary>A "play all" playlist strings the episodes together.</summary>
    PlayAll,

    /// <summary>The HDMV menu's buttons play them.</summary>
    MenuButtons,

    /// <summary>The Java menu's files name them: a guess.</summary>
    JavaMenu,

    /// <summary>The order of their clips: no word from the disc.</summary>
    Clips
}

/// <summary>
/// Finds a TV disc's episodes: the group of playlists of about the same length (within a quarter),
/// five minutes or more, that holds the most of the disc's data, leaving out duplicates, "play all"
/// playlists, reels of deleted scenes and fakes that loop tiny clips (Doctor Who's 12 four-hour playlists of 100 MB), in disc
/// order. Data, not the number of playlists: a disc's featurettes often outnumber its episodes
/// (Doctor Who S1 disc 3: three 45-minute 11 GB episodes, thirteen 12-minute 800 MB featurettes),
/// but the episodes are where the disc spends its bytes.
/// </summary>
public static class EpisodeFinder
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMinutes(5);

    /// <summary>How far an episode's length may be from another's in the same group.</summary>
    public const double Spread = 0.25;

    public static List<PlaylistInfo> Find(DiscAnalysis analysis)
    {
        var pool = analysis.Playlists
            .Where(p => p.PartOf == null && p.DuplicateOf == null && !p.PlayAll && !p.TooLittleData && p.Reel == 0 && p.Duration >= Minimum)
            .OrderBy(p => p.Duration).ToList();
        List<PlaylistInfo> best = [];
        for (var i = 0; i < pool.Count; i++)
        {
            // Every run of playlists, shortest first, none more than a quarter longer than the shortest: a
            // group around one playlist would let a longer special pull it off and drop the shortest episode.
            var limit = pool[i].Duration.TotalSeconds * (1 + Spread);
            var group = pool.Skip(i).TakeWhile(o => o.Duration.TotalSeconds <= limit).ToList();
            // A lone playlist (the film on a film disc) is no group of episodes.
            if (group.Count >= 2 && Better(group, best))
            {
                best = group;
            }
        }

        if (best.Count > 0)
        {
            best.AddRange(Longer(pool, best));
        }

        return best.Count > 0 ? InDiscOrder(analysis, best).Episodes : [];
    }

    /// <summary>A longer episode (a special) is at most this many times the group's usual length.</summary>
    public const double LongerUpTo = 2;

    /// <summary>
    /// The playlists a bit shorter or up to twice as long as the episodes (a feature-length Christmas
    /// special, a double episode) made like them: same picture, about the same data rate.
    /// </summary>
    private static IEnumerable<PlaylistInfo> Longer(List<PlaylistInfo> pool, List<PlaylistInfo> group)
    {
        var lengths = group.Select(p => p.Duration.TotalSeconds).Order().ToList();
        var usual = lengths[lengths.Count / 2];
        var rates = group.Where(p => p.Size > 0).Select(p => p.BitsPerSecond).Order().ToList();
        var rate = rates.Count > 0 ? rates[rates.Count / 2] : 0;
        var resolution = group.GroupBy(p => p.Resolution).OrderByDescending(g => g.Count()).First().Key;
        return pool.Where(p => !group.Contains(p)
                               && p.Duration.TotalSeconds >= usual * (1 - Spread) && p.Duration.TotalSeconds <= usual * LongerUpTo
                               && p.Resolution == resolution
                               && (rate == 0 || p.Size > 0 && p.BitsPerSecond >= rate * 0.5 && p.BitsPerSecond <= rate * 2));
    }

    // More bytes wins; with no sizes known (or equal ones), more playlists, then more running time.
    private static bool Better(List<PlaylistInfo> group, List<PlaylistInfo> best)
    {
        var bytes = group.Sum(p => p.Size).CompareTo(best.Sum(p => p.Size));
        if (bytes != 0)
        {
            return bytes > 0;
        }

        return group.Count != best.Count
            ? group.Count > best.Count
            : group.Sum(p => p.Duration.TotalSeconds) > best.Sum(p => p.Duration.TotalSeconds);
    }

    /// <summary>
    /// The episodes in the order the disc plays them: as a "play all" playlist strings them
    /// together when there is one, else as the disc's menu lists them once read, else by the number of their first clip (discs are authored in
    /// order; playlist numbers often are not), then by playlist number. Says which it used.
    /// </summary>
    public static (List<PlaylistInfo> Episodes, string How) InDiscOrder(DiscAnalysis analysis, List<PlaylistInfo> episodes)
    {
        var playAll = PlayAll(analysis, episodes);
        if (playAll.Playlist != null)
        {
            return (episodes.Select((e, i) => (e, playAll.Positions[i])).OrderBy(x => x.Item2).Select(x => x.e).ToList(),
                $"in the order of the disc's \"play all\" playlist {playAll.Playlist.Name}");
        }

        // The menu's buttons (or the Java menu's list), when they name every episode.
        if (MenuCovers(analysis, episodes) && analysis.MenuOrder is { } menu)
        {
            return (episodes.OrderBy(e => menu.IndexOf(e.Name)).ToList(), analysis.MenuNote ?? "in the order of the disc menu");
        }

        var byClip = episodes
            .OrderBy(e => e.Mpls.Clips.FirstOrDefault() ?? "", StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var byName = episodes.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        return (byClip, byClip.SequenceEqual(byName)
            ? "in playlist order, which is also the order of their clips"
            : "in the order of their clips on the disc (the playlist numbers are out of order)");
    }

    /// <summary>Where <see cref="InDiscOrder"/> takes the order of these episodes from.</summary>
    public static DiscOrderSource SourceOf(DiscAnalysis analysis, List<PlaylistInfo> episodes) =>
        PlayAll(analysis, episodes).Playlist != null ? DiscOrderSource.PlayAll
        : MenuCovers(analysis, episodes) ? analysis.MenuIsGuess ? DiscOrderSource.JavaMenu : DiscOrderSource.MenuButtons
        : DiscOrderSource.Clips;

    private static bool MenuCovers(DiscAnalysis analysis, List<PlaylistInfo> episodes) =>
        analysis.MenuOrder is { } menu && episodes.Count >= 2 && episodes.All(e => menu.Contains(e.Name));

    /// <summary>The longest "play all" playlist that plays every one of the episodes, and where each starts in it.</summary>
    public static (PlaylistInfo? Playlist, List<int> Positions) PlayAll(DiscAnalysis analysis, IReadOnlyList<PlaylistInfo> episodes) =>
        analysis.Playlists
            .Where(p => p.PlayAll && p.DuplicateOf == null)
            .Select(p => (Playlist: (PlaylistInfo?)p, Positions: episodes.Select(e => Position(p.Mpls.Clips, e.Mpls.Clips)).ToList()))
            .Where(x => x.Positions.All(i => i >= 0))
            .OrderByDescending(x => x.Playlist!.Duration)
            .FirstOrDefault();

    // Where a run of clips starts inside a longer list of clips, or -1.
    private static int Position(IReadOnlyList<string> all, IReadOnlyList<string> part)
    {
        for (var i = 0; part.Count > 0 && i + part.Count <= all.Count; i++)
        {
            if (part.Select((c, j) => string.Equals(c, all[i + j], StringComparison.OrdinalIgnoreCase)).All(x => x))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the disc looks like a TV disc: three or more episodes, and nothing else (but a "play
    /// all" or a duplicate) much longer than them, as a film would be.
    /// </summary>
    public static bool LooksLikeTv(DiscAnalysis analysis)
    {
        var episodes = Find(analysis);
        if (episodes.Count < 3)
        {
            return false;
        }

        var longest = episodes.Max(p => p.Duration);
        return longest < TimeSpan.FromMinutes(75) && !analysis.Playlists.Any(p =>
            p.PartOf == null && p.DuplicateOf == null && !p.PlayAll && !p.TooLittleData && p.Reel == 0 && !episodes.Contains(p) && p.Duration > longest * 1.5);
    }
}
