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

    [TestMethod]
    public void FlagsPlayAllPlaylists()
    {
        var disc = Disc(
            Playlist("00800", MplsBuilder.Feature(("00055", 5400))),
            Playlist("00010", MplsBuilder.Feature(("00002", 600))),
            Playlist("00011", MplsBuilder.Feature(("00003", 300))),
            Playlist("00012", MplsBuilder.Feature(("00004", 120))),
            Playlist("00020", MplsBuilder.Feature(("00002", 600), ("00003", 300), ("00004", 120))),
            // Two clips, but only one of them plays on its own: not a "play all".
            Playlist("00030", MplsBuilder.Feature(("00002", 600), ("00009", 900))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsTrue(disc.Find("00020")!.PlayAll);
        Assert.IsFalse(disc.Find("00010")!.PlayAll);
        Assert.IsFalse(disc.Find("00030")!.PlayAll);
        Assert.AreEqual("00800", disc.MainFeature?.Name);
    }

    private static MplsBuilder Cut(string clip, uint from, uint to)
    {
        var builder = new MplsBuilder();
        builder.Items.Add(new MplsBuilder.Item(clip, 27_000_000 + from * 45000, 27_000_000 + to * 45000));
        return builder;
    }

    [TestMethod]
    public void FindsEpisodesCutOutOfOneClip()
    {
        // A "play all" of the whole clip, three 22-minute episodes cut out of it, an extra and a menu.
        var disc = Disc(
            Playlist("00001", Cut("00055", 0, 3960)),
            Playlist("00002", Cut("00055", 0, 1320)),
            Playlist("00003", Cut("00055", 1320, 2640)),
            Playlist("00004", Cut("00055", 2640, 3960)),
            Playlist("00005", MplsBuilder.Feature(("00002", 240))),
            Playlist("00006", MplsBuilder.Feature(("00003", 15))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsNull(disc.Find("00003")!.DuplicateOf, "same length and clip, but another part of it: not a duplicate");
        Assert.IsTrue(disc.Find("00001")!.PlayAll);
        CollectionAssert.AreEqual(new[] { "00002", "00003", "00004" }, EpisodeFinder.Find(disc).Select(p => p.Name).ToArray());
        Assert.IsTrue(EpisodeFinder.LooksLikeTv(disc));
    }

    [TestMethod]
    public void AFilmWithFeaturettesIsNotATvDisc()
    {
        var disc = Disc(
            Playlist("00800", MplsBuilder.Feature(("00055", 7200))),
            Playlist("00010", MplsBuilder.Feature(("00002", 1200))),
            Playlist("00011", MplsBuilder.Feature(("00003", 1100))),
            Playlist("00012", MplsBuilder.Feature(("00004", 1300))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.HasCount(3, EpisodeFinder.Find(disc));
        Assert.IsFalse(EpisodeFinder.LooksLikeTv(disc), "the film is much longer than the 'episodes'");
    }

    [TestMethod]
    public void ReadsADoctorWhoStyleDisc()
    {
        // Five episodes, each also listed with a second-long clip after it, and a "four hour"
        // playlist that loops tiny clips. Playlist numbers are out of order; the clips are not.
        var sizes = new Dictionary<string, long> { ["00099"] = 2_000_000, ["00500"] = 300_000, ["00501"] = 300_000 };
        var playlists = new List<PlaylistInfo>();
        var episodes = new[] { ("00153", "00030"), ("00152", "00031"), ("00118", "00010"), ("00117", "00011"), ("00154", "00032") };
        var extra = 157;
        foreach (var (name, clip) in episodes)
        {
            sizes[clip] = 8_500_000_000;
            playlists.Add(Playlist(name, MplsBuilder.Feature((clip, 2800))));
            playlists.Add(Playlist($"{extra++:00000}", MplsBuilder.Feature((clip, 2800), ("00099", 1))));
        }

        playlists.Add(Playlist("00167", MplsBuilder.Feature(("00500", 7500), ("00501", 7500))));
        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = playlists,
            ClipSizes = sizes
        };
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.AreEqual("00153", disc.Find("00157")!.DuplicateOf, "the episode plus a tiny clip repeats the plain one");
        Assert.IsTrue(disc.Find("00167")!.TooLittleData);
        CollectionAssert.AreEqual(new[] { "00118", "00117", "00153", "00152", "00154" },
            EpisodeFinder.Find(disc).Select(p => p.Name).ToArray(), "in clip order, fakes and repeats left out");
        Assert.IsTrue(EpisodeFinder.LooksLikeTv(disc), "the fake four-hour playlist is not a film");
        StringAssert.Contains(EpisodeFinder.InDiscOrder(disc, EpisodeFinder.Find(disc)).How, "playlist numbers are out of order");
    }

    [TestMethod]
    public void PicksTheEpisodesOverMoreNumerousFeaturettes()
    {
        // Doctor Who S1 disc 3: three 45-minute 1080p episodes of 11 GB, thirteen 10-14 minute
        // 480i featurettes of about 800 MB. More featurettes than episodes, far less data.
        var sizes = new Dictionary<string, long>();
        var playlists = new List<PlaylistInfo>();
        foreach (var (name, clip, seconds) in new[] { ("00152", "00052", 2851), ("00117", "00017", 2713), ("00118", "00018", 2680) })
        {
            sizes[clip] = 11_000_000_000;
            playlists.Add(Playlist(name, MplsBuilder.Feature((clip, (double)seconds))));
        }

        var featurettes = new[] { 725, 676, 669, 659, 651, 782, 774, 849, 793, 756, 580, 660, 697 };
        for (var i = 0; i < featurettes.Length; i++)
        {
            var clip = $"{200 + i:00000}";
            sizes[clip] = 800_000_000;
            playlists.Add(Playlist($"{170 + i:00000}", MplsBuilder.Feature((clip, (double)featurettes[i]))));
        }

        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = playlists,
            ClipSizes = sizes
        };
        disc.Classify(TimeSpan.FromMinutes(2));

        CollectionAssert.AreEqual(new[] { "00117", "00118", "00152" }, EpisodeFinder.Find(disc).Select(p => p.Name).ToArray());
        Assert.IsTrue(EpisodeFinder.LooksLikeTv(disc));
    }

    [TestMethod]
    public void TakesTheChristmasSpecialAndEveryEpisode()
    {
        // Doctor Who S2 disc 1: the 61-minute Christmas special and four 46-minute episodes, all about
        // 8-11 GB, plus two short SD extras. A group around the special left out the shortest episode.
        var sizes = new Dictionary<string, long>();
        var playlists = new List<PlaylistInfo>();
        foreach (var (name, clip, seconds, bytes) in new[]
                 {
                     ("00160", "00060", 3690, 10_800_000_000L), ("00168", "00068", 2802, 8_210_000_000L), ("00025", "00025", 2792, 8_210_000_000L),
                     ("00071", "00071", 2771, 8_130_000_000L), ("00161", "00061", 2763, 8_090_000_000L), ("00119", "00119", 440, 334_000_000L),
                     ("00115", "00115", 278, 204_000_000L)
                 })
        {
            sizes[clip] = bytes;
            playlists.Add(Playlist(name, MplsBuilder.Feature((clip, (double)seconds))));
        }

        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = playlists,
            ClipSizes = sizes
        };
        disc.Classify(TimeSpan.FromMinutes(2));

        CollectionAssert.AreEquivalent(new[] { "00025", "00071", "00160", "00161", "00168" }, EpisodeFinder.Find(disc).Select(p => p.Name).ToArray());
        Assert.IsTrue(EpisodeFinder.LooksLikeTv(disc));
    }

    [TestMethod]
    public void AnEpisodeWithALongerClipAddedIsNotARepeat()
    {
        var disc = Disc(
            Playlist("00800", MplsBuilder.Feature(("00055", 2800))),
            Playlist("00801", MplsBuilder.Feature(("00055", 2800), ("00057", 60))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsNull(disc.Find("00801")!.DuplicateOf);
    }

    [TestMethod]
    public void OrdersEpisodesLikeThePlayAll()
    {
        var disc = Disc(
            Playlist("00010", MplsBuilder.Feature(("00004", 1300))),
            Playlist("00011", MplsBuilder.Feature(("00002", 1320))),
            Playlist("00012", MplsBuilder.Feature(("00003", 1310))),
            Playlist("00020", MplsBuilder.Feature(("00003", 1310), ("00004", 1300), ("00002", 1320))));
        disc.Classify(TimeSpan.FromMinutes(2));

        var (order, how) = EpisodeFinder.InDiscOrder(disc, EpisodeFinder.Find(disc));
        CollectionAssert.AreEqual(new[] { "00012", "00010", "00011" }, order.Select(p => p.Name).ToArray());
        StringAssert.Contains(how, "00020");
    }
}
