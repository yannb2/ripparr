using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class ReelTests
{
    private static PlaylistInfo Playlist(string name, MplsBuilder builder) =>
        new() { Name = name, Mpls = Mpls.Parse(builder.Build()) };

    private static DiscAnalysis Disc(params PlaylistInfo[] playlists)
    {
        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
            Playlists = playlists.ToList(),
            ClipSizes = new Dictionary<string, long>
            {
                ["00055"] = 30_000_000_000, ["00201"] = 300_000_000, ["00202"] = 500_000_000, ["00203"] = 200_000_000, ["00099"] = 1_000_000
            }
        };
        disc.Classify(TimeSpan.FromMinutes(2));
        return disc;
    }

    // Three deleted scenes back to back, with a one-second blank before the second, and a chapter at each.
    private static MplsBuilder DeletedScenes()
    {
        var reel = MplsBuilder.Feature(("00201", 90), ("00099", 1), ("00202", 150), ("00203", 60));
        reel.Video.Add(new MplsWriter.Stream(0x1B, 0x1011, Format: 6, Rate: 3));
        reel.Audio.Add(new MplsWriter.Stream(0x80, 0x1100, "eng", Format: 6, Rate: 1));
        reel.Marks.Add((0, 27_000_000));
        reel.Marks.Add((2, 27_000_000));
        reel.Marks.Add((2, 27_000_000 + 75 * 45000));
        reel.Marks.Add((3, 27_000_000));
        return reel;
    }

    [TestMethod]
    public void FindsAReelOfDeletedScenes()
    {
        var disc = Disc(Playlist("00800", MplsBuilder.Feature(("00055", 7200))), Playlist("00060", DeletedScenes()));

        Assert.AreEqual(3, disc.Find("00060")!.Reel, "the one-second blank is no piece");
        Assert.AreEqual(0, disc.Find("00800")!.Reel);
        CollectionAssert.AreEqual(new[] { 1, 3, 4 }, DiscAnalysis.Pieces(disc.Find("00060")!).Select(x => x.Number).ToArray());
    }

    [TestMethod]
    public void CutsAPieceOutAsAPlaylistOfItsOwn()
    {
        var disc = Disc(Playlist("00800", MplsBuilder.Feature(("00055", 7200))), Playlist("00060", DeletedScenes()));

        var piece = disc.Find("00060-p03")!;
        Assert.AreEqual("00060", piece.PartOf);
        Assert.AreEqual("00060, piece 3", piece.Label);
        Assert.AreEqual("00060", piece.FileName, "the job writes it as the reel's playlist in its work folder");
        Assert.AreEqual(TimeSpan.FromSeconds(150), piece.Duration);
        CollectionAssert.AreEqual(new[] { "00202" }, piece.Mpls.Clips.ToArray());
        Assert.AreEqual(500_000_000, piece.Size);
        CollectionAssert.AreEqual(new[] { 0x1011, 0x1100 }, piece.Mpls.Streams.Select(s => s.Pid).ToArray(), "the streams as the disc lists them");
        CollectionAssert.AreEqual(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(75) }, piece.Mpls.Chapters.Select(c => c.Start).ToArray());
        Assert.IsNotNull(piece.MadePlaylist);
        Assert.AreSame(piece, disc.Find("00060-p03"), "made once");
    }

    [TestMethod]
    public void APieceWithoutAChapterGetsOneAtItsStart()
    {
        var reel = MplsBuilder.Feature(("00201", 90), ("00202", 150), ("00203", 60));
        var disc = Disc(Playlist("00060", reel));

        CollectionAssert.AreEqual(new[] { TimeSpan.Zero }, disc.Find("00060-p02")!.Mpls.Chapters.Select(c => c.Start).ToArray());
        Assert.IsNull(disc.Find("00060-p09"));
        Assert.IsNull(disc.Find("00800-p01"));
    }

    [TestMethod]
    public void APlayAllOrALongPieceIsNoReel()
    {
        var disc = Disc(
            Playlist("00800", MplsBuilder.Feature(("00055", 7200))),
            Playlist("00010", MplsBuilder.Feature(("00201", 90))),
            Playlist("00011", MplsBuilder.Feature(("00202", 150))),
            Playlist("00012", MplsBuilder.Feature(("00203", 60))),
            Playlist("00020", MplsBuilder.Feature(("00201", 90), ("00202", 150), ("00203", 60))),
            Playlist("00030", MplsBuilder.Feature(("00201", 90), ("00202", 900), ("00203", 60))));

        Assert.IsTrue(disc.Find("00020")!.PlayAll);
        Assert.AreEqual(0, disc.Find("00020")!.Reel, "its pieces are playlists already");
        Assert.AreEqual(0, disc.Find("00030")!.Reel, "a 15-minute piece is no deleted scene");
    }

    [TestMethod]
    public void AReelIsNoEpisode()
    {
        // Three 22-minute episodes and a 21-minute reel of seven 3-minute scenes.
        var playlists = new List<PlaylistInfo>
        {
            Playlist("00001", MplsBuilder.Feature(("00011", 1320))),
            Playlist("00002", MplsBuilder.Feature(("00012", 1310))),
            Playlist("00003", MplsBuilder.Feature(("00013", 1330))),
            Playlist("00009", MplsBuilder.Feature(Enumerable.Range(0, 7).Select(i => ($"{300 + i:00000}", 180.0)).ToArray()))
        };
        var disc = Disc(playlists.ToArray());

        Assert.AreEqual(7, disc.Find("00009")!.Reel);
        CollectionAssert.AreEqual(new[] { "00001", "00002", "00003" }, EpisodeFinder.Find(disc).Select(p => p.Name).ToArray());
    }
}
