namespace Ripparr.Disc;

/// <summary>
/// Finds a TV disc's episodes: the biggest group of playlists of about the same length (within a
/// quarter), five minutes or more, leaving out duplicates, "play all" playlists and fakes that loop
/// tiny clips (Doctor Who's 12 four-hour playlists of 100 MB), in disc order.
/// </summary>
public static class EpisodeFinder
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMinutes(5);

    /// <summary>How far an episode's length may be from another's in the same group.</summary>
    public const double Spread = 0.25;

    public static List<PlaylistInfo> Find(DiscAnalysis analysis)
    {
        var pool = analysis.Playlists.Where(p => p.DuplicateOf == null && !p.PlayAll && !p.TooLittleData && p.Duration >= Minimum).ToList();
        List<PlaylistInfo> best = [];
        foreach (var playlist in pool)
        {
            var length = playlist.Duration.TotalSeconds;
            var group = pool.Where(o => Math.Abs(o.Duration.TotalSeconds - length) <= length * Spread).ToList();
            if (group.Count > best.Count || group.Count == best.Count && group.Sum(p => p.Duration.TotalSeconds) > best.Sum(p => p.Duration.TotalSeconds))
            {
                best = group;
            }
        }

        return best.Count >= 2 ? InDiscOrder(analysis, best).Episodes : [];
    }

    /// <summary>
    /// The episodes in the order the disc plays them: as a "play all" playlist strings them
    /// together when there is one, else by the number of their first clip (discs are authored in
    /// order; playlist numbers often are not), then by playlist number. Says which it used.
    /// </summary>
    public static (List<PlaylistInfo> Episodes, string How) InDiscOrder(DiscAnalysis analysis, List<PlaylistInfo> episodes)
    {
        var playAll = analysis.Playlists
            .Where(p => p.PlayAll && p.DuplicateOf == null)
            .Select(p => (Playlist: p, Positions: episodes.Select(e => Position(p.Mpls.Clips, e.Mpls.Clips)).ToList()))
            .Where(x => x.Positions.All(i => i >= 0))
            .OrderByDescending(x => x.Playlist.Duration)
            .FirstOrDefault();
        if (playAll.Playlist != null)
        {
            return (episodes.Select((e, i) => (e, playAll.Positions[i])).OrderBy(x => x.Item2).Select(x => x.e).ToList(),
                $"in the order of the disc's \"play all\" playlist {playAll.Playlist.Name}");
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
            p.DuplicateOf == null && !p.PlayAll && !p.TooLittleData && !episodes.Contains(p) && p.Duration > longest * 1.5);
    }
}
