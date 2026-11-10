namespace Ripparr.Disc;

/// <summary>Where a TV disc's episode order comes from, the disc's own word first.</summary>
public enum DiscOrderSource
{
    /// <summary>A "play all" playlist strings the episodes together.</summary>
    PlayAll,

    /// <summary>A "play all" playlist strings some of them together (the rest are placed by their clips).</summary>
    PlayAllPart,

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
    /// together when one plays them all, else as the disc's menu lists them once read, else by the number of their first clip (discs are authored in
    /// order; playlist numbers often are not), then by playlist number; a "play all" that plays only
    /// some of them puts those in its order among the others. Says which it used.
    /// </summary>
    public static (List<PlaylistInfo> Episodes, string How) InDiscOrder(DiscAnalysis analysis, List<PlaylistInfo> episodes)
    {
        var playAll = PlayAll(analysis, episodes);
        if (playAll is { Complete: true })
        {
            return (playAll.Order, $"in the order of the disc's \"play all\" playlist {playAll.Playlist.Name}");
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
        if (playAll != null)
        {
            // The ones the "play all" plays take its order, in the places the clip order gives them.
            var places = byClip.Select((e, i) => (e, i)).Where(x => !playAll.Missing.Contains(x.e)).Select(x => x.i).ToList();
            for (var i = 0; i < places.Count; i++)
            {
                byClip[places[i]] = playAll.Order[i];
            }

            return (byClip, $"{playAll.Order.Count} of {episodes.Count} in the order of the disc's \"play all\" playlist {playAll.Playlist.Name}, " +
                            $"{string.Join(", ", playAll.Missing.Select(m => m.Label))} (not in it) by {(playAll.Missing.Count == 1 ? "its clip" : "their clips")}");
        }

        var byName = episodes.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        return (byClip, byClip.SequenceEqual(byName)
            ? "in playlist order, which is also the order of their clips"
            : "in the order of their clips on the disc (the playlist numbers are out of order)");
    }

    /// <summary>Where <see cref="InDiscOrder"/> takes the order of these episodes from.</summary>
    public static DiscOrderSource SourceOf(DiscAnalysis analysis, List<PlaylistInfo> episodes)
    {
        var playAll = PlayAll(analysis, episodes);
        return playAll is { Complete: true } ? DiscOrderSource.PlayAll
            : MenuCovers(analysis, episodes) ? analysis.MenuIsGuess ? DiscOrderSource.JavaMenu : DiscOrderSource.MenuButtons
            : playAll != null ? DiscOrderSource.PlayAllPart
            : DiscOrderSource.Clips;
    }

    private static bool MenuCovers(DiscAnalysis analysis, List<PlaylistInfo> episodes) =>
        analysis.MenuOrder is { } menu && episodes.Count >= 2 && episodes.All(e => menu.Contains(e.Name));

    /// <summary>
    /// A "play all" playlist and what it says about the episodes: the ones it plays, in its order,
    /// where each starts in it, the ones it does not play, and whether its chapters mark each start.
    /// </summary>
    public sealed record PlayAllMatch(PlaylistInfo Playlist, List<PlaylistInfo> Order, List<TimeSpan> Starts, List<PlaylistInfo> Missing, bool ChaptersAgree)
    {
        /// <summary>It plays every one of the episodes.</summary>
        public bool Complete => Missing.Count == 0;

        /// <summary>Where the playlist comes in the "play all" (1 = first), 0 when it does not play it.</summary>
        public int PlaceOf(PlaylistInfo playlist) => Order.IndexOf(playlist) + 1;
    }

    /// <summary>Longest the clips of an episode a "play all" leaves out (its own logo, a warning) may run.</summary>
    public static readonly TimeSpan OwnIntro = TimeSpan.FromSeconds(30);

    /// <summary>A chapter mark this close to an episode's start in the "play all" marks it.</summary>
    public static readonly TimeSpan ChapterSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The "play all" playlist that plays the most of the episodes (two at least), the longest on a
    /// tie, or null. An episode plays in it when its clips do, back to back, within the in and out
    /// times of the "play all"'s items: episodes cut out of one big clip are told apart (and
    /// ordered) by where they start in it. An episode with a logo or warning of its own (up to
    /// <see cref="OwnIntro"/>) that the "play all" leaves out still counts when its longest clip plays in it.
    /// </summary>
    public static PlayAllMatch? PlayAll(DiscAnalysis analysis, IReadOnlyList<PlaylistInfo> episodes)
    {
        PlayAllMatch? best = null;
        foreach (var all in analysis.Playlists.Where(p => p.PlayAll && p.DuplicateOf == null && !episodes.Contains(p)))
        {
            var found = episodes.Select(e => (Episode: e, Start: StartIn(all.Mpls.Items, e.Mpls.Items)))
                .Where(x => x.Start != null)
                .OrderBy(x => x.Start)
                .ToList();
            if (found.Count < 2)
            {
                continue;
            }

            var starts = found.Select(x => TimeSpan.FromSeconds(x.Start!.Value / (double)Mpls.ClockRate)).ToList();
            var chapters = all.Mpls.Chapters;
            var match = new PlayAllMatch(all, found.Select(x => x.Episode).ToList(), starts,
                episodes.Where(e => found.All(x => x.Episode != e)).ToList(),
                chapters.Count > 0 && starts.All(s => chapters.Any(c => (c.Start - s).Duration() <= ChapterSlack)));
            if (best == null || match.Order.Count > best.Order.Count
                             || match.Order.Count == best.Order.Count && all.Duration > best.Playlist.Duration)
            {
                best = match;
            }
        }

        return best;
    }

    // Where (45 kHz ticks from its start) an episode's items start playing inside a "play all"'s
    // items, or null: all of them back to back, else its longest one.
    internal static long? StartIn(IReadOnlyList<PlayItem> all, IReadOnlyList<PlayItem> part)
    {
        if (part.Count == 0)
        {
            return null;
        }

        for (var start = 0; start + part.Count <= all.Count; start++)
        {
            if (Enumerable.Range(0, part.Count).All(i => Inside(all[start + i], part[i])))
            {
                return Offset(all, start) + (part[0].InTime - all[start].InTime);
            }
        }

        // Only when the rest is a logo or a warning: a playlist that shares one clip of several is no episode of it.
        var longest = part.Select((item, i) => (item, i)).MaxBy(x => x.item.Duration45k);
        if (part.Sum(i => i.Duration45k) - longest.item.Duration45k > OwnIntro.TotalSeconds * Mpls.ClockRate)
        {
            return null;
        }

        for (var at = 0; at < all.Count; at++)
        {
            if (Inside(all[at], longest.item))
            {
                // Back to where the episode would start, before its own shorter items.
                var before = part.Take(longest.i).Sum(i => i.Duration45k);
                return Math.Max(0, Offset(all, at) + (longest.item.InTime - all[at].InTime) - before);
            }
        }

        return null;
    }

    private static bool Inside(PlayItem outer, PlayItem inner) =>
        string.Equals(outer.Clip, inner.Clip, StringComparison.OrdinalIgnoreCase)
        && outer.InTime <= inner.InTime && inner.OutTime <= outer.OutTime;

    private static long Offset(IReadOnlyList<PlayItem> items, int index) => items.Take(index).Sum(i => i.Duration45k);

    /// <summary>
    /// The playlists whose episode numbers go against the "play all"'s order: a lower number (in
    /// the same season) than one the "play all" plays before it.
    /// </summary>
    public static List<PlaylistInfo> AgainstPlayAll(PlayAllMatch playAll, Func<PlaylistInfo, (int Season, int Episode)?> number)
    {
        var against = new List<PlaylistInfo>();
        var highest = new Dictionary<int, int>();
        foreach (var playlist in playAll.Order)
        {
            if (number(playlist) is not { } n)
            {
                continue;
            }

            if (highest.TryGetValue(n.Season, out var before) && n.Episode < before)
            {
                against.Add(playlist);
            }
            else
            {
                highest[n.Season] = n.Episode;
            }
        }

        return against;
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
