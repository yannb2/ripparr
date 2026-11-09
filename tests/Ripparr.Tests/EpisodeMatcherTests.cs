using Ripparr.Arr;
using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class EpisodeMatcherTests
{
    private static PlaylistInfo Playlist(string name, double minutes) =>
        new() { Name = name, Mpls = Mpls.Parse(MplsBuilder.Feature(($"{name}", minutes * 60)).Build()) };

    private static EpisodeInfo Episode(int season, int number, int? runtime, string aired = "2005-06-01") =>
        new(season, number, null, $"S{season}E{number}") { Runtime = runtime, AirDate = DateOnly.Parse(aired) };

    [TestMethod]
    public void NumbersEpisodesByRunningTime()
    {
        // Disc order 00117, 00118, 00152; broadcast order is 00152, 00117, 00118.
        var disc = new[] { Playlist("00117", 44.2), Playlist("00118", 51.5), Playlist("00152", 38.9) };
        var known = new[] { Episode(1, 10, 45), Episode(1, 11, 39), Episode(1, 12, 44), Episode(1, 13, 52) };

        var (matches, note) = EpisodeMatcher.Find(disc, known, 1, 11);

        Assert.IsNotNull(matches);
        CollectionAssert.AreEqual(new[] { 12, 13, 11 }, matches.Select(m => m.Episode.Episode).ToArray());
        StringAssert.Contains(note, "running time");
    }

    [TestMethod]
    public void KeepsTheDiscOrderWhenRunningTimesCannotTell()
    {
        var disc = new[] { Playlist("00117", 45.2), Playlist("00118", 44.7), Playlist("00152", 47.5) };

        var same = new[] { Episode(1, 11, 45), Episode(1, 12, 45), Episode(1, 13, 45) };
        Assert.IsNull(EpisodeMatcher.Find(disc, same, 1, 11).Matches, "TVDB gives them all 45 minutes");

        var unknown = new[] { Episode(1, 11, null), Episode(1, 12, 45), Episode(1, 13, 45) };
        StringAssert.Contains(EpisodeMatcher.Find(disc, unknown, 1, 11).Note, "no running time");

        var agree = new[] { Episode(1, 11, 45), Episode(1, 12, 44), Episode(1, 13, 47) };
        StringAssert.Contains(EpisodeMatcher.Find(disc, agree, 1, 11).Note, "agree");
    }

    [TestMethod]
    public void FindsASpecialOfTheSeason()
    {
        // Two episodes and the Christmas special on the same disc.
        var disc = new[] { Playlist("00001", 44.5), Playlist("00002", 60.3), Playlist("00003", 45.4) };
        var known = new[]
        {
            Episode(1, 12, 45, "2005-06-11"), Episode(1, 13, 45, "2005-06-18"), Episode(1, 14, 46, "2005-06-25"),
            Episode(0, 1, 60, "2005-12-25"), Episode(0, 2, 61, "2010-12-25")
        };

        var (matches, note) = EpisodeMatcher.Find(disc, known, 1, 12);

        Assert.IsNotNull(matches);
        CollectionAssert.AreEqual(new[] { "S1E12", "S0E1", "S1E13" }, matches.Select(m => m.Episode.Title).ToArray(),
            "the special aired after the season; the 2010 one is too late to be on this disc");
        StringAssert.Contains(note, "special");
        CollectionAssert.AreEqual(new[] { 1 }, EpisodeMatcher.Specials(known, 1).Select(e => e.Episode).ToArray());
    }

    [TestMethod]
    public void TheHungarianMethodFindsTheCheapestAssignment()
    {
        var cost = new double[,] { { 4, 1, 3 }, { 2, 0, 5 }, { 3, 2, 2 } };
        CollectionAssert.AreEqual(new[] { 1, 0, 2 }, EpisodeMatcher.Hungarian(cost));
    }

    [TestMethod]
    public void WarnsWhenTheMenuAndTheRunningTimesDisagree()
    {
        var disc = new[] { Playlist("00117", 44.2), Playlist("00118", 51.5), Playlist("00152", 38.9) };
        var known = new[] { Episode(1, 11, 39), Episode(1, 12, 44), Episode(1, 13, 52) };
        var (matches, _) = EpisodeMatcher.Find(disc, known, 1, 11);

        var warning = EpisodeMatcher.Compare("k", disc, matches, known, 1, 11, DiscOrderSource.MenuButtons);

        Assert.IsNotNull(warning);
        CollectionAssert.AreEqual(new[] { 11, 12, 13 }, warning.Disc.Select(m => m.Episode.Episode).ToArray());
        CollectionAssert.AreEqual(new[] { 12, 13, 11 }, warning.RunningTimes.Select(m => m.Episode.Episode).ToArray());
        Assert.IsTrue(Enumerable.Range(0, 3).All(warning.Differs));
        Assert.IsTrue(warning.Suggestions.Any(s => s.Contains("Watch")));
        Assert.IsTrue(warning.Suggestions.Any(s => s.Contains("aired order")));
        Assert.IsTrue(warning.Suggestions.Any(s => s.Contains("13 min shorter than S01E13")), "00152 (39 min) is 13 min short of E13's 52");
        Assert.AreEqual("S01E11 S1E11 (39 min)", EpisodeMatcher.Describe(warning.Disc[0].Episode));

        Assert.IsNull(EpisodeMatcher.Compare("k", disc, matches, known, 1, 11, DiscOrderSource.Clips), "clip order is no word from the disc");
        Assert.IsNull(EpisodeMatcher.Compare("k", disc, null, known, 1, 11, DiscOrderSource.MenuButtons), "they agree");
    }

    [TestMethod]
    public void SaysWhenOnlyTwoTradePlaces()
    {
        var disc = new[] { Playlist("00001", 45), Playlist("00002", 58), Playlist("00003", 50) };
        var known = new[] { Episode(1, 1, 45), Episode(1, 2, 50), Episode(1, 3, 58) };
        var (matches, _) = EpisodeMatcher.Find(disc, known, 1, 1);

        var warning = EpisodeMatcher.Compare("k", disc, matches, known, 1, 1, DiscOrderSource.JavaMenu);

        Assert.IsNotNull(warning);
        Assert.IsFalse(warning.Differs(0));
        StringAssert.Contains(warning.Suggestions[0], "trade places");
        Assert.IsTrue(warning.Suggestions.Any(s => s.Contains("Java menu")));
    }

    [TestMethod]
    public void TheOrderSourceIsTheDiscsWordFirst()
    {
        var episodes = new List<PlaylistInfo> { Playlist("00002", 45), Playlist("00001", 45) };
        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = episodes
        };
        Assert.AreEqual(DiscOrderSource.Clips, EpisodeFinder.SourceOf(disc, episodes));

        disc.MenuOrder = ["00001", "00002"];
        Assert.AreEqual(DiscOrderSource.MenuButtons, EpisodeFinder.SourceOf(disc, episodes));

        disc.MenuIsGuess = true;
        Assert.AreEqual(DiscOrderSource.JavaMenu, EpisodeFinder.SourceOf(disc, episodes));
    }

    private static readonly EpisodeInfo[] DoctorWhoSeason2 =
    [
        Episode(2, 1, 45, "2006-04-15"), Episode(2, 2, 45, "2006-04-22"), Episode(2, 3, 45, "2006-04-29"), Episode(2, 4, 45, "2006-05-06"),
        Episode(2, 5, 45, "2006-05-13"), Episode(0, 1, 7, "2005-11-18"), Episode(0, 2, 60, "2005-12-25"), Episode(0, 3, 13, "2005-12-25"),
        Episode(0, 4, 60, "2006-12-25"), Episode(0, 5, 45, "2007-06-30"), Episode(0, 6, 8, "2007-11-16")
    ];

    // Doctor Who S2 disc 1, in playlist order: the Christmas special (00160) and four episodes.
    private static readonly PlaylistInfo[] DoctorWhoDisc =
        [Playlist("00025", 46.5), Playlist("00071", 46.2), Playlist("00160", 61.5), Playlist("00161", 46.05), Playlist("00168", 46.7)];

    [TestMethod]
    public void TheChristmasSpecialBeforeTheSeasonIsFound()
    {
        var (matches, note) = EpisodeMatcher.Find(DoctorWhoDisc, DoctorWhoSeason2, 2, 1);

        Assert.IsNotNull(matches, note);
        CollectionAssert.AreEqual(new[] { "S2E1", "S2E2", "S0E2", "S2E3", "S2E4" }, matches.Select(m => m.Episode.Title).ToArray(),
            "aired in December, four months before the season; the next Christmas special is as long but further away");
    }

    [TestMethod]
    public void RunningTimesThatFitNothingKeepTheDiscOrder()
    {
        // The season was left on 0: the specials from S00E03 are nothing like these playlists.
        var four = DoctorWhoDisc.Where(p => p.Name != "00161").ToArray();
        var (matches, note) = EpisodeMatcher.Find(four, DoctorWhoSeason2, 0, 3);

        Assert.IsNull(matches);
        StringAssert.Contains(note, "fit none");
    }
}
