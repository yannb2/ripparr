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
}
