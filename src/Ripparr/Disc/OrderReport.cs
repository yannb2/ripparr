using Ripparr.Media;

namespace Ripparr.Disc;

/// <summary>How sure Ripparr is of a TV disc's episode order.</summary>
public enum OrderConfidence
{
    /// <summary>The disc plays them in this order itself: a "play all" playlist.</summary>
    Sure,

    /// <summary>The disc's menu buttons list them, or a "play all" plays some of them.</summary>
    Likely,

    /// <summary>Read out of a Java menu's files, or only the order of the clips.</summary>
    Guess
}

/// <summary>
/// What the TV episodes card says about the order: whether a "play all" playlist was found and
/// what it plays, where the order comes from and how sure that is.
/// </summary>
public sealed record OrderReport(DiscOrderSource Source, OrderConfidence Confidence, EpisodeFinder.PlayAllMatch? PlayAll, string PlayAllLine, string SourceLine)
{
    public static OrderReport For(DiscAnalysis analysis, List<PlaylistInfo> episodes)
    {
        var playAll = EpisodeFinder.PlayAll(analysis, episodes);
        var source = EpisodeFinder.SourceOf(analysis, episodes);
        var order = EpisodeFinder.InDiscOrder(analysis, episodes).Episodes;
        // The menu (or the clips) put the ones the "play all" plays in another order than it does.
        var against = playAll != null && !order.Where(playAll.Order.Contains).SequenceEqual(playAll.Order);

        var playAllLine = playAll == null
            ? analysis.IsDvd
                ? "No title on this DVD plays these episodes one after the other."
                : "No playlist on this disc plays these episodes one after the other."
            : DescribePlayAll(playAll, episodes.Count, analysis.IsDvd);

        var (confidence, sourceLine) = source switch
        {
            DiscOrderSource.PlayAll => (OrderConfidence.Sure, "The episodes are numbered in that order, the disc's own."),
            DiscOrderSource.PlayAllPart => (OrderConfidence.Likely,
                $"Those are numbered in its order; {Names(playAll!.Missing)} {(playAll.Missing.Count == 1 ? "takes its place" : "take their places")} by {(playAll.Missing.Count == 1 ? "its clip" : "their clips")}, a guess."),
            DiscOrderSource.MenuButtons => against
                ? (OrderConfidence.Guess, "Numbered in the order of the disc menu's buttons, but the \"play all\" plays them in another order: check with Watch.")
                : (OrderConfidence.Likely, playAll != null
                    ? "Numbered in the order of the disc menu's buttons, which the \"play all\" agrees with."
                    : "Numbered in the order of the disc menu's buttons."),
            DiscOrderSource.JavaMenu => (OrderConfidence.Guess, "Numbered in the order the Java menu's files name them: a guess."),
            _ => (OrderConfidence.Guess, analysis.IsDvd
                ? "Numbered in the order of the titles on the DVD: a guess, check it on the DVD's menu."
                : "Numbered in the order of their clips on the disc: a guess, check with Watch.")
        };

        return new OrderReport(source, confidence, playAll, playAllLine, sourceLine);
    }

    private static string DescribePlayAll(EpisodeFinder.PlayAllMatch playAll, int count, bool dvd)
    {
        var what = $"{(dvd ? "Title" : "Playlist")} {playAll.Playlist.Label} ({Format.Duration(playAll.Playlist.Duration)})";
        var chain = string.Join(" → ", playAll.Order.Select(p => p.Label));
        var line = playAll.Complete
            ? $"{what} plays {(count == 2 ? "both" : $"all {count}")} episodes one after the other: {chain}."
            : $"{what} plays {playAll.Order.Count} of the {count} episodes one after the other: {chain}. Not in it: {Names(playAll.Missing)}.";
        return playAll.ChaptersAgree ? line + " Its chapters start at each episode." : line;
    }

    private static string Names(List<PlaylistInfo> playlists) => string.Join(", ", playlists.Select(p => p.Label));
}
