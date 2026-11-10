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

    /// <summary>More than this many minutes off per playlist on average, even at best: the running times fit no episodes here.</summary>
    public const double MaximumAverageGap = 5;

    // Ties (TVDB gives most episodes of a series the same runtime) keep the disc order: squared, so a
    // special taking one place shifts the rest by one rather than swapping two of them.
    private const double OrderTieBreak = 0.01;

    // Two specials as long as each other (Christmas specials are all an hour): the one aired nearer the season.
    private const double DayTieBreak = 0.0005;

    public sealed record Match(PlaylistInfo Playlist, EpisodeInfo Episode);

    /// <summary>The note when the running times give every playlist the episode the disc order does.</summary>
    public const string AgreeNote = "Running times agree with the disc order.";

    /// <summary>The note when the running times are too close to tell the episodes apart.</summary>
    public const string TooCloseNote = "Running times are too close to tell the episodes apart: numbered in disc order.";

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
                            ? Gap(playlists[row], columns[column]) + OrderTieBreak * (row - column) * (row - column)
                            : Gap(playlists[row], columns[column]) + SpecialPenalty + DayTieBreak * DaysAway(columns[column], known, season);
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
        if (bestGaps / count > MaximumAverageGap)
        {
            return (null, $"Sonarr's running times fit none of these episodes well ({bestGaps / count:0} min off each at best): numbered in disc order. " +
                          "Check the season and the first episode.");
        }
        if (matches.Select((m, i) => m.Episode == regular[i]).All(x => x))
        {
            return (null, AgreeNote);
        }

        if (inOrder - bestGaps < MinimumGain)
        {
            return (null, TooCloseNote);
        }

        var specialCount = matches.Count(m => m.Episode.Season != season);
        return (matches, $"Numbered by running time against Sonarr's ({bestGaps:0.#} min off in all, {inOrder:0.#} in disc order)" +
                         (specialCount > 0 ? $", {specialCount} as a special" : "") + ".");
    }

    /// <summary>
    /// The disc's order and the running times give some playlists different episodes: both, row by
    /// row (in disc order), and what to check.
    /// </summary>
    public sealed record Disagreement(string Key, DiscOrderSource Source, List<Match> Disc, List<Match> RunningTimes, List<string> Suggestions)
    {
        public bool Differs(int row) => Disc[row].Episode != RunningTimes[row].Episode;
    }

    /// <summary>A playlist this many minutes off the runtime of the episode the disc order gives it is pointed out.</summary>
    public const double FarOff = 4;

    /// <summary>
    /// When the disc says something about the order (a "play all" playlist, the menu) and the
    /// running times <see cref="Find"/> found say otherwise: the two side by side, with suggestions.
    /// Null when they agree, or when the order is only the clips' (nothing from the disc to weigh).
    /// </summary>
    public static Disagreement? Compare(string key, IReadOnlyList<PlaylistInfo> playlists, List<Match>? matches, IReadOnlyList<EpisodeInfo> known,
        int season, int first, DiscOrderSource source)
    {
        var regular = known.Where(e => e.Season == season && e.Episode >= first).OrderBy(e => e.Episode).Take(playlists.Count).ToList();
        if (matches == null || source == DiscOrderSource.Clips || regular.Count < playlists.Count)
        {
            return null;
        }

        var disc = playlists.Select((p, i) => new Match(p, regular[i])).ToList();
        var rows = Enumerable.Range(0, disc.Count).Where(i => disc[i].Episode != matches[i].Episode).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var suggestions = new List<string>();
        if (rows.Count == 2 && disc[rows[0]].Episode == matches[rows[1]].Episode && disc[rows[1]].Episode == matches[rows[0]].Episode)
        {
            suggestions.Add($"Only {disc[rows[0]].Playlist.Label} and {disc[rows[1]].Playlist.Label} trade places: watch the start of those two, the title settles it.");
        }
        else
        {
            suggestions.Add("Watch the start of the playlists in bold: the title (or the \"previously\" recap) settles it.");
        }

        foreach (var special in matches.Where(m => m.Episode.Season != season))
        {
            suggestions.Add($"Running times make {special.Playlist.Label} the special {Code(special.Episode)} \"{special.Episode.Title}\". " +
                            "If the menu lists it with the episodes, it is more likely an episode about as long as that special.");
        }

        var worst = rows.Select(i => disc[i]).OrderByDescending(m => Gap(m.Playlist, m.Episode)).First();
        var gap = Gap(worst.Playlist, worst.Episode);
        if (gap >= FarOff)
        {
            var way = worst.Playlist.Duration.TotalMinutes > worst.Episode.Runtime ? "longer" : "shorter";
            suggestions.Add($"{worst.Playlist.Label} ({worst.Playlist.Duration.TotalMinutes:0} min) is {gap:0} min {way} than {Code(worst.Episode)} in Sonarr " +
                            "(the disc order's episode for it): if that runtime is right, it is not that episode.");
        }

        suggestions.Add(source == DiscOrderSource.JavaMenu
            ? "The Java menu's order is read out of its files, a guess: the running times are more likely right unless Watch says otherwise."
            : source == DiscOrderSource.PlayAllPart
            ? "The \"play all\" plays only some of these: their order is the disc's, but where the others go is a guess from their clips."
            : "Sonarr numbers episodes in TVDB's aired order. A box set in production or DVD order lists them otherwise: then the running times are right for Sonarr.");
        suggestions.Add("TVDB's runtimes are sometimes rounded, or one episode's copied to all: check them on TheTVDB if both orders look odd.");
        suggestions.Add($"Check the first episode (E{first:00}): if the disc starts elsewhere, change it and press Match running times again.");

        return new Disagreement(key, source, disc, matches, suggestions);
    }

    /// <summary>"S01E05 Dalek (45 min)".</summary>
    public static string Describe(EpisodeInfo episode) =>
        $"{Code(episode)} {episode.Title}" + (episode.Runtime is { } runtime ? $" ({runtime} min)" : "");

    public static string Code(EpisodeInfo episode) => $"S{episode.Season:00}E{episode.Episode:00}";

    /// <summary>Days between a special's air date and the season's first to last episode (0 inside it or unknown).</summary>
    private static double DaysAway(EpisodeInfo special, IReadOnlyList<EpisodeInfo> known, int season)
    {
        var dates = known.Where(e => e.Season == season && e.AirDate != null).Select(e => e.AirDate!.Value).ToList();
        if (special.AirDate is not { } aired || dates.Count == 0)
        {
            return 0;
        }

        return aired < dates.Min() ? dates.Min().DayNumber - aired.DayNumber
            : aired > dates.Max() ? aired.DayNumber - dates.Max().DayNumber
            : 0;
    }

    /// <summary>
    /// The specials (season 0) that aired from six months before the season (the Christmas special
    /// before a spring season) to a year after its last episode.
    /// </summary>
    public static List<EpisodeInfo> Specials(IReadOnlyList<EpisodeInfo> known, int season)
    {
        var dates = known.Where(e => e.Season == season && e.AirDate != null).Select(e => e.AirDate!.Value).ToList();
        return known
            .Where(e => e.Season == 0 && e.Runtime != null && season != 0)
            .Where(e => dates.Count == 0 || e.AirDate == null
                        || e.AirDate >= dates.Min().AddMonths(-6) && e.AirDate <= dates.Max().AddYears(1))
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
