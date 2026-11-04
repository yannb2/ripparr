using Ripparr.Disc;
using Ripparr.Jobs;

namespace Ripparr.Tests;

[TestClass]
public class MplsTests
{
    [TestMethod]
    public void ReadsItemsDurationAndClips()
    {
        var mpls = Mpls.Parse(MplsBuilder.UhdFilm(120).Build());

        Assert.AreEqual("0300", mpls.Version);
        CollectionAssert.AreEqual(new[] { "00055", "00056" }, mpls.Clips.ToArray());
        Assert.AreEqual(7200, mpls.Duration.TotalSeconds, 0.01);
    }

    [TestMethod]
    public void LayerInTheFilmsOwnClipsHasNoSeparateClips()
    {
        var mpls = Mpls.Parse(MplsBuilder.UhdFilm().Build());

        Assert.IsNull(mpls.EnhancementLayerClips);
        Assert.IsEmpty(mpls.SubPaths);
    }

    [TestMethod]
    public void ReadsALayerKeptInASubPath()
    {
        var builder = MplsBuilder.UhdFilm();
        builder.LayerClips.AddRange(["00057", "00058"]);
        var mpls = Mpls.Parse(builder.Build());

        var layer = mpls.Streams.Single(s => s.DolbyVisionEnhancementLayer);
        Assert.AreEqual(0x1015, layer.Pid);
        Assert.AreEqual(0, layer.SubPath);
        Assert.HasCount(1, mpls.SubPaths);
        Assert.AreEqual(10, mpls.SubPaths[0].Type);
        CollectionAssert.AreEqual(new[] { "00057", "00058" }, mpls.EnhancementLayerClips!.ToArray());
        // The film itself and its chapters are read as before.
        CollectionAssert.AreEqual(new[] { "00055", "00056" }, mpls.Clips.ToArray());
        Assert.HasCount(4, mpls.Chapters);
    }

    [TestMethod]
    public void ListsThePidsFfmpegSees()
    {
        const string output = """
            Input #0, mpegts, from '00004.m2ts':
              Stream #0:0[0x1011]: Video: hevc (Main 10) (HDMV / 0x564D4448), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x2160
              Stream #0:1[0x1100]: Audio: dts (DTS-HD MA) ([134][0][0][0] / 0x0086), 48000 Hz, 5.1(side)
              Stream #0:2[0x1200]: Subtitle: hdmv_pgs_subtitle ([144][0][0][0] / 0x0090)
            """;

        CollectionAssert.AreEquivalent(new[] { 0x1011, 0x1100, 0x1200 }, JobPipeline.StreamPids(output).ToArray());
    }

    [TestMethod]
    public void FindsTheLayerByItsCodecWhenThePlaylistPidIsWrong()
    {
        // The Return of the Pink Panther UHD: the playlist says 0x1500, the clip carries the layer as 0x1015.
        const string output = """
              Stream #0:0[0x1011]: Video: hevc (Main 10) (HDMV / 0x564D4448), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x2160
              Stream #0:1[0x1015]: Video: hevc (Main 10) (HDMV / 0x564D4448), yuv420p10le(tv), 1920x1080
              Stream #0:2[0x1100]: Audio: dts (DTS-HD MA) ([134][0][0][0] / 0x0086), 48000 Hz, 5.1(side)
              Stream #0:3[0x12a0]: Subtitle: hdmv_pgs_subtitle ([144][0][0][0] / 0x0090)
            """;

        CollectionAssert.AreEqual(new[] { 0x1011, 0x1015 }, JobPipeline.HevcPids(output).ToArray());

        var mpls = Mpls.Parse(MplsBuilder.UhdFilm().Build()).WithLayerPid(0x1015);
        Assert.AreEqual(0x1015, mpls.Streams.Single(s => s.DolbyVisionEnhancementLayer).Pid);
        Assert.AreEqual(0x1011, mpls.Streams.First(s => s.Kind == StreamKind.Video).Pid);
        Assert.HasCount(4, mpls.Chapters);
    }

    [TestMethod]
    public void ReadsStreamsWithLanguagesAndTheDolbyVisionLayer()
    {
        var mpls = Mpls.Parse(MplsBuilder.UhdFilm().Build());

        var video = mpls.Streams.Where(s => s.Kind == StreamKind.Video).ToList();
        Assert.HasCount(2, video);
        Assert.AreEqual("HEVC", video[0].CodecName);
        Assert.AreEqual("2160p", StreamCoding.Resolution(video[0].Format));
        Assert.AreEqual((24000, 1001), StreamCoding.FrameRate(video[0].Rate));
        Assert.IsFalse(video[0].DolbyVisionEnhancementLayer);
        Assert.IsTrue(video[1].DolbyVisionEnhancementLayer);
        Assert.AreEqual(0x1015, video[1].Pid);
        Assert.IsTrue(mpls.HasDolbyVisionEnhancementLayer);

        var audio = mpls.Streams.Where(s => s.Kind == StreamKind.Audio).ToList();
        CollectionAssert.AreEqual(new[] { "TrueHD", "AC3", "DTS-HD MA", "AC3" }, audio.Select(a => a.CodecName).ToArray());
        CollectionAssert.AreEqual(new[] { "eng", "eng", "fra", "eng" }, audio.Select(a => a.Language).ToArray());
        Assert.AreEqual("Multi", StreamCoding.AudioLayout(audio[0].Format));
        Assert.AreEqual("2.0", StreamCoding.AudioLayout(audio[3].Format));
        Assert.AreEqual(0x1100, audio[0].Pid);

        var subtitles = mpls.Streams.Where(s => s.Kind == StreamKind.Subtitle).ToList();
        CollectionAssert.AreEqual(new[] { "eng", "fra", "spa" }, subtitles.Select(s => s.Language).ToArray());
    }

    [TestMethod]
    public void ChapterTimesRunAcrossPlayItems()
    {
        var mpls = Mpls.Parse(MplsBuilder.UhdFilm(120).Build());

        // Item 0 is 3600 s long: the marks at the start and 900 s into item 1 land at 3600 s and 4500 s.
        CollectionAssert.AreEqual(new[] { 0.0, 600, 3600, 4500 }, mpls.Chapters.Select(c => c.Start.TotalSeconds).ToArray());
    }

    [TestMethod]
    public void RejectsOtherFiles()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Mpls.Parse("NOPE0200........................................"u8.ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(() => Mpls.Parse("MPLS0200"u8.ToArray()));
    }
}
