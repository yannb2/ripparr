namespace Ripparr.Disc;

/// <summary>
/// Finds a TV disc's episodes: the biggest group of playlists of about the same length (within a
/// quarter), five minutes or more, leaving out duplicates and "play all" playlists, in disc order.
/// </summary>
public static class EpisodeFinder
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMinutes(5);

    /// <summary>How far an episode's length may be from another's in the same group.</summary>
    public const double Spread = 0.25;

    public static List<PlaylistInfo> Find(DiscAnalysis analysis)
    {
        var pool = analysis.Playlists.Where(p => p.DuplicateOf == null && !p.PlayAll && p.Duration >= Minimum).ToList();
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

        return best.Count >= 2 ? best.OrderBy(p => p.Name, StringComparer.Ordinal).ToList() : [];
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
            p.DuplicateOf == null && !p.PlayAll && !episodes.Contains(p) && p.Duration > longest * 1.5);
    }
}
