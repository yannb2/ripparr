using Ripparr.Arr;

namespace Ripparr.Tests;

[TestClass]
public class ArrAddTests
{
    private static readonly List<RootFolder> Roots =
        ArrAdd.ParseRoots("""[{"path":"/tv/live-action/","freeSpace":1000},{"path":"/tv/anime/"},{"path":"/tv/animated/"},{"path":""}]""");

    [TestMethod]
    public void ReadsRootFoldersAndProfiles()
    {
        Assert.HasCount(3, Roots);
        Assert.AreEqual(1000, Roots.First(r => r.Path == "/tv/live-action/").FreeSpace);
        var profiles = ArrAdd.ParseProfiles("""[{"id":4,"name":"Remux-1080p"},{"id":0,"name":"broken"}]""");
        Assert.AreEqual(new QualityProfile(4, "Remux-1080p"), profiles.Single());
    }

    [TestMethod]
    public void PicksTheCategoryFolder()
    {
        Assert.AreEqual("/tv/live-action/", ArrAdd.GuessRoot(Roots, animation: false, anime: false));
        Assert.AreEqual("/tv/animated/", ArrAdd.GuessRoot(Roots, animation: true, anime: false));
        Assert.AreEqual("/tv/anime/", ArrAdd.GuessRoot(Roots, animation: true, anime: true));
        Assert.AreEqual("/data/tv", ArrAdd.GuessRoot([new RootFolder("/data/tv", null)], false, true), "no category folders: the first");
        Assert.IsNull(ArrAdd.GuessRoot([], false, false));
    }

    [TestMethod]
    public void ReadsGenres()
    {
        var series = SonarrClient.ParseSeries("""[{"title":"Lady Oscar","year":1979,"tvdbId":77,"genres":["Animation","Drama"],"originalLanguage":{"name":"Japanese"}}]""").Single();
        Assert.IsTrue(series.Animation);
        Assert.IsNull(series.SonarrId);
        var movie = RadarrClient.Parse("""[{"title":"Heat","year":1995,"tmdbId":949,"genres":["Crime"]}]""").Single();
        Assert.IsFalse(movie.Animation);
    }
}
