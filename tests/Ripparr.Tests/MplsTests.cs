using Ripparr.Disc;

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
