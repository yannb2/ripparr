using Ripparr.Disc;

namespace Ripparr.Tests;

/// <summary>Finding a TV disc's "play all" playlist and what it says about the episode order.</summary>
[TestClass]
public class PlayAllOrderTests
{
    private static PlaylistInfo Playlist(string name, MplsBuilder builder) =>
        new() { Name = name, Mpls = Mpls.Parse(builder.Build()) };

    private static DiscAnalysis Disc(params PlaylistInfo[] playlists) =>
        new()
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = playlists.ToList()
        };

    // Seconds from..to of one clip.
    private static MplsBuilder Cut(string clip, uint from, uint to)
    {
        var builder = new MplsBuilder();
        builder.Items.Add(new MplsBuilder.Item(clip, 27_000_000 + from * 45000, 27_000_000 + to * 45000));
        return builder;
    }

    [TestMethod]
    public void OrdersEpisodesCutOutOfOneClipByWhereTheyStart()
    {
        // The playlist numbers are out of order: 00004 is the first part of the clip.
        var disc = Disc(
            Playlist("00001", Cut("00055", 0, 3960)),
            Playlist("00002", Cut("00055", 1320, 2650)),
            Playlist("00003", Cut("00055", 2650, 3960)),
            Playlist("00004", Cut("00055", 0, 1320)));
        disc.Classify(TimeSpan.FromMinutes(2));

        var episodes = EpisodeFinder.Find(disc);
        CollectionAssert.AreEqual(new[] { "00004", "00002", "00003" }, episodes.Select(p => p.Name).ToArray());
        var playAll = EpisodeFinder.PlayAll(disc, episodes)!;
        Assert.AreEqual("00001", playAll.Playlist.Name);
        Assert.IsTrue(playAll.Complete);
        Assert.AreEqual(TimeSpan.FromSeconds(1320), playAll.Starts[1]);
        Assert.AreEqual(2, playAll.PlaceOf(disc.Find("00002")!));
    }

    [TestMethod]
    public void ReportsAPlayAllAsSure()
    {
        var builder = MplsBuilder.Feature(("00003", 1310), ("00004", 1300), ("00002", 1320));
        builder.Marks.Add((0, 27_000_000));
        builder.Marks.Add((1, 27_000_000));
        builder.Marks.Add((2, 27_000_000));
        var disc = Disc(
            Playlist("00010", MplsBuilder.Feature(("00004", 1300))),
            Playlist("00011", MplsBuilder.Feature(("00002", 1320))),
            Playlist("00012", MplsBuilder.Feature(("00003", 1310))),
            Playlist("00020", builder));
        disc.Classify(TimeSpan.FromMinutes(2));

        var report = OrderReport.For(disc, EpisodeFinder.Find(disc));
        Assert.AreEqual(OrderConfidence.Sure, report.Confidence);
        Assert.AreEqual(DiscOrderSource.PlayAll, report.Source);
        StringAssert.Contains(report.PlayAllLine, "00012 → 00010 → 00011");
        StringAssert.Contains(report.PlayAllLine, "chapters start at each episode");
    }

    [TestMethod]
    public void APlayAllOfSomeEpisodesOrdersThoseAndPlacesTheRestByClip()
    {
        // The play all leaves out the special (00030, clip 00001, first on the disc).
        var disc = Disc(
            Playlist("00030", MplsBuilder.Feature(("00001", 2700))),
            Playlist("00010", MplsBuilder.Feature(("00004", 2650))),
            Playlist("00011", MplsBuilder.Feature(("00002", 2640))),
            Playlist("00012", MplsBuilder.Feature(("00003", 2660))),
            Playlist("00020", MplsBuilder.Feature(("00003", 2660), ("00004", 2650), ("00002", 2640))));
        disc.Classify(TimeSpan.FromMinutes(2));

        var episodes = EpisodeFinder.Find(disc);
        CollectionAssert.AreEqual(new[] { "00030", "00012", "00010", "00011" }, episodes.Select(p => p.Name).ToArray());
        var report = OrderReport.For(disc, episodes);
        Assert.AreEqual(DiscOrderSource.PlayAllPart, report.Source);
        Assert.AreEqual(OrderConfidence.Likely, report.Confidence);
        Assert.IsFalse(report.PlayAll!.Complete);
        StringAssert.Contains(report.PlayAllLine, "3 of the 4");
        StringAssert.Contains(report.PlayAllLine, "Not in it: 00030");
    }

    [TestMethod]
    public void NoPlayAllIsAGuess()
    {
        var disc = Disc(
            Playlist("00010", MplsBuilder.Feature(("00002", 1300))),
            Playlist("00011", MplsBuilder.Feature(("00003", 1320))),
            Playlist("00012", MplsBuilder.Feature(("00004", 1310))));
        disc.Classify(TimeSpan.FromMinutes(2));

        var report = OrderReport.For(disc, EpisodeFinder.Find(disc));
        Assert.IsNull(report.PlayAll);
        Assert.AreEqual(OrderConfidence.Guess, report.Confidence);
        StringAssert.Contains(report.PlayAllLine, "No playlist");
    }

    [TestMethod]
    public void TheMenuAndAPartPlayAllThatDisagreeAreAGuess()
    {
        var disc = Disc(
            Playlist("00030", MplsBuilder.Feature(("00001", 2700))),
            Playlist("00010", MplsBuilder.Feature(("00004", 2650))),
            Playlist("00011", MplsBuilder.Feature(("00002", 2640))),
            Playlist("00020", MplsBuilder.Feature(("00004", 2650), ("00002", 2640))));
        disc.Classify(TimeSpan.FromMinutes(2));
        var episodes = EpisodeFinder.Find(disc);

        disc.MenuOrder = ["00030", "00010", "00011"];
        Assert.AreEqual(OrderConfidence.Likely, OrderReport.For(disc, episodes).Confidence, "the menu agrees with the play all");

        disc.MenuOrder = ["00030", "00011", "00010"];
        var report = OrderReport.For(disc, episodes);
        Assert.AreEqual(DiscOrderSource.MenuButtons, report.Source);
        Assert.AreEqual(OrderConfidence.Guess, report.Confidence);
    }

    [TestMethod]
    public void AnEpisodeWithItsOwnLogoStillCounts()
    {
        // Each episode starts with a 5 s logo clip the play all leaves out.
        var disc = Disc(
            Playlist("00010", MplsBuilder.Feature(("00009", 5), ("00002", 1300))),
            Playlist("00011", MplsBuilder.Feature(("00009", 5), ("00003", 1320))),
            Playlist("00020", MplsBuilder.Feature(("00003", 1320), ("00002", 1300))));
        disc.Classify(TimeSpan.FromMinutes(2));

        var episodes = disc.Playlists.Where(p => p.Name != "00020").ToList();
        var playAll = EpisodeFinder.PlayAll(disc, episodes)!;
        CollectionAssert.AreEqual(new[] { "00011", "00010" }, playAll.Order.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void FindsNumbersThatGoAgainstThePlayAll()
    {
        var disc = Disc(
            Playlist("00010", MplsBuilder.Feature(("00002", 1300))),
            Playlist("00011", MplsBuilder.Feature(("00003", 1320))),
            Playlist("00012", MplsBuilder.Feature(("00004", 1310))),
            Playlist("00020", MplsBuilder.Feature(("00002", 1300), ("00003", 1320), ("00004", 1310))));
        disc.Classify(TimeSpan.FromMinutes(2));
        var playAll = EpisodeFinder.PlayAll(disc, EpisodeFinder.Find(disc))!;

        var numbers = new Dictionary<string, int> { ["00010"] = 1, ["00011"] = 3, ["00012"] = 2 };
        var against = EpisodeFinder.AgainstPlayAll(playAll, p => (1, numbers[p.Name]));
        CollectionAssert.AreEqual(new[] { "00012" }, against.Select(p => p.Name).ToArray());

        numbers["00011"] = 0;
        Assert.IsEmpty(EpisodeFinder.AgainstPlayAll(playAll, p => p.Name == "00011" ? (0, 1) : (1, numbers[p.Name])), "a special has its own numbers");
    }
}
