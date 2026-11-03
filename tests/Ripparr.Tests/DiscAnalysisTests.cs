using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class DiscAnalysisTests
{
    private static PlaylistInfo Playlist(string name, MplsBuilder builder) =>
        new() { Name = name, Mpls = Mpls.Parse(builder.Build()) };

    private static DiscAnalysis Disc(params PlaylistInfo[] playlists) =>
        new()
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = playlists.ToList(),
            ClipSizes = new Dictionary<string, long> { ["00055"] = 40_000_000_000, ["00056"] = 30_000_000_000, ["00057"] = 5_000_000_000, ["00001"] = 1000 }
        };

    [TestMethod]
    public void PicksTheLongestAsMainFeatureAndSizesIt()
    {
        var disc = Disc(
            Playlist("00800", MplsBuilder.Feature(("00055", 3600), ("00056", 3000))),
            Playlist("00001", MplsBuilder.Feature(("00001", 30))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.AreEqual("00800", disc.MainFeature?.Name);
        Assert.AreEqual(70_000_000_000, disc.MainFeature!.Size);
        Assert.IsTrue(disc.Find("00001")!.Short);
    }

    [TestMethod]
    public void MarksDuplicatesAndOtherCuts()
    {
        var disc = Disc(
            Playlist("00800", MplsBuilder.Feature(("00055", 3600), ("00056", 3000))),
            Playlist("00801", MplsBuilder.Feature(("00055", 3600), ("00056", 3000))),
            Playlist("00802", MplsBuilder.Feature(("00055", 3600), ("00057", 600), ("00056", 3000))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.AreEqual("00802", disc.MainFeature?.Name, "the longer cut is the main feature");
        Assert.AreEqual("00800", disc.Find("00801")!.DuplicateOf);
        Assert.IsTrue(disc.Find("00800")!.PossibleCut);
        Assert.IsFalse(disc.ManyPlaylists);
    }

    [TestMethod]
    public void NoticesDiscsThatHideTheFilm()
    {
        var playlists = Enumerable.Range(0, 10)
            .Select(i => Playlist($"{800 + i:00000}", MplsBuilder.Feature(($"{i:00000}", 6000 + i * 3))))
            .ToArray();
        var disc = Disc(playlists);
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsTrue(disc.ManyPlaylists);
    }
}
