using Ripparr.Arr;

namespace Ripparr.Disc;

/// <summary>
/// Numbers a disc's episodes by their running time against Sonarr's (TVDB's) runtimes, when the disc
/// order and the broadcast order differ: the disc's playlists take the next episodes of the season
/// (from the first one on this disc), each the one whose runtime fits it best, and a playlist far
/// from all of them but close to a special of that season (aired around it) becomes that special.
/// </summary>
public static class EpisodeMatcher
{
    /// <summary>A playlist is only moved to a special when it fits it this much better than an episode (minutes).</summary>
    public const double SpecialPenalty = 3;

    /// <summary>Running times must win by this much in all over the disc order (minutes), or the disc order stays.</summary>
    public const double MinimumGain = 2;

    // Ties (TVDB gives most episodes of a series the same runtime) keep the disc order.
    private const double OrderTieBreak = 0.01;

    public sealed record Match(PlaylistInfo Playlist, EpisodeInfo Episode);

    /// <summary>The matches, in disc order, and a line saying what was done; Matches is null when the disc order stays.</summary>
    public static (List<Match>? Matches, string Note) Find(IReadOnlyList<PlaylistInfo> playlists, IReadOnlyList<EpisodeInfo> known, int season, int first)
    {
        var count = playlists.Count;
        var regular = known.Where(e => e.Season == season && e.Episode >= first).OrderBy(e => e.Episode).Take(count).ToList();
        if (count < 2 || regular.Count < count)
        {
            return (null, "");
        }

        if (regular.Count(e => e.Runtime != null) < count)
        {
            return (null, "Sonarr has no running time for some of these episodes: numbered in disc order.");
        }

        var specials = Specials(known, season);
        var inOrder = Enumerable.Range(0, count).Sum(i => Gap(playlists[i], regular[i]));

        (double Cost, List<Match> Matches)? best = null;
        for (var k = 0; k <= Math.Min(count - 1, specials.Count); k++)
        {
            var episodes = regular.Take(count - k).ToList();
            var columns = episodes.Concat(specials).ToList();
            var size = columns.Count;
            var cost = new double[size, size];
            for (var row = 0; row < size; row++)
            {
                for (var column = 0; column < size; column++)
                {
                    cost[row, column] = row >= count
                        // Rows past the playlists take the specials no playlist uses.
                        ? column < episodes.Count ? double.PositiveInfinity : 0
                        : column < episodes.Count
                            ? Gap(playlists[row], columns[column]) + OrderTieBreak * Math.Abs(row - column)
                            : Gap(playlists[row], columns[column]) + SpecialPenalty;
                }
            }

            var assignment = Hungarian(cost);
            var total = Enumerable.Range(0, count).Sum(row => cost[row, assignment[row]]);
            if (best == null || total < best.Value.Cost - 1e-9)
            {
                best = (total, Enumerable.Range(0, count).Select(row => new Match(playlists[row], columns[assignment[row]])).ToList());
            }
        }

        var matches = best!.Value.Matches;
        var bestGaps = matches.Sum(m => Gap(m.Playlist, m.Episode));
        if (matches.Select((m, i) => m.Episode == regular[i]).All(x => x))
        {
            return (null, "Running times agree with the disc order.");
        }

        if (inOrder - bestGaps < MinimumGain)
        {
            return (null, "Running times are too close to tell the episodes apart: numbered in disc order.");
        }

        var specialCount = matches.Count(m => m.Episode.Season != season);
        return (matches, $"Numbered by running time against Sonarr's ({bestGaps:0.#} min off in all, {inOrder:0.#} in disc order)" +
                         (specialCount > 0 ? $", {specialCount} as a special" : "") + ".");
    }

    /// <summary>The specials (season 0) that aired from three months before the season to a year after its last episode.</summary>
    public static List<EpisodeInfo> Specials(IReadOnlyList<EpisodeInfo> known, int season)
    {
        var dates = known.Where(e => e.Season == season && e.AirDate != null).Select(e => e.AirDate!.Value).ToList();
        return known
            .Where(e => e.Season == 0 && e.Runtime != null && season != 0)
            .Where(e => dates.Count == 0 || e.AirDate == null
                        || e.AirDate >= dates.Min().AddMonths(-3) && e.AirDate <= dates.Max().AddYears(1))
            .OrderBy(e => e.Episode)
            .ToList();
    }

    /// <summary>Minutes between the playlist's length and the episode's runtime.</summary>
    public static double Gap(PlaylistInfo playlist, EpisodeInfo episode) =>
        episode.Runtime is { } runtime ? Math.Abs(playlist.Duration.TotalMinutes - runtime) : 60;

    /// <summary>The square cost matrix's cheapest assignment (Hungarian method): the column of each row.</summary>
    public static int[] Hungarian(double[,] cost)
    {
        var n = cost.GetLength(0);
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1]; // the row matched to each column, 1-based; 0 = none
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minimum = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = 0;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    var reduced = Finite(cost[i0 - 1, j - 1]) - u[i0] - v[j];
                    if (reduced < minimum[j])
                    {
                        minimum[j] = reduced;
                        way[j] = j0;
                    }

                    if (minimum[j] < delta)
                    {
                        delta = minimum[j];
                        j1 = j;
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minimum[j] -= delta;
                    }
                }

                j0 = j1;
            } while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var result = new int[n];
        for (var j = 1; j <= n; j++)
        {
            result[p[j] - 1] = j - 1;
        }

        return result;
    }

    // "Never" as a big number: the method's sums stay finite.
    private static double Finite(double value) => double.IsPositiveInfinity(value) ? 1e9 : value;
}
