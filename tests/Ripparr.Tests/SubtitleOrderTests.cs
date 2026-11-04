using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class SubtitleOrderTests
{
    private static OutputTrack Sub(string language, string codec, bool forced = false, bool sdh = false, bool commentary = false, string name = "") =>
        new(new PlannedTrack
        {
            Kind = StreamKind.Subtitle, Language = language, Codec = codec, Forced = forced, HearingImpaired = sdh, Commentary = commentary, Keep = true
        }, new DemuxedFile(name, new StreamFacts()), name);

    private static OutputTrack Video() =>
        new(new PlannedTrack { Kind = StreamKind.Video, Codec = "HEVC", Keep = true }, new DemuxedFile("video", new StreamFacts()), "video");

    [TestMethod]
    public void CleanSrtGoesBeforeTheSdhPictureTrack()
    {
        // As the job leaves them: the SDH-free SRT right after the SDH PGS it was read from.
        var outputs = new List<OutputTrack>
        {
            Video(), Sub("eng", "PGS", sdh: true, name: "sdh"), Sub("eng", "SRT", name: "clean"), Sub("eng", "PGS", forced: true, name: "forced"),
            Sub("fre", "PGS", name: "french"), Sub("eng", "PGS", commentary: true, name: "commentary")
        };

        var sorted = SubtitleOrder.Sort(outputs, new RipProfile());

        CollectionAssert.AreEqual(new[] { "video", "forced", "clean", "sdh", "commentary", "french" }, sorted.Select(o => o.Name).ToArray());
    }

    [TestMethod]
    public void FollowsTheProfilesKindsAndTextFirst()
    {
        var profile = new RipProfile { SubtitleKindOrder = ["full", "sdh", "forced"], SrtBeforePictures = true };
        var outputs = new List<OutputTrack>
        {
            Sub("eng", "PGS", forced: true, name: "forced"), Sub("eng", "PGS", name: "pgs"), Sub("eng", "SRT", name: "srt"),
            Sub("eng", "PGS", commentary: true, name: "commentary")
        };

        var sorted = SubtitleOrder.Sort(outputs, profile);

        // Commentary was missing from the profile's list: it goes last, where the default puts it.
        CollectionAssert.AreEqual(new[] { "srt", "pgs", "forced", "commentary" }, sorted.Select(o => o.Name).ToArray());
    }

    [TestMethod]
    public void FrenchCanadaStaysWithFrench()
    {
        var outputs = new List<OutputTrack> { Sub("fre", "PGS", sdh: true, name: "sdh"), Sub("eng", "PGS", name: "eng"), Sub("fr-CA", "SRT", name: "clean") };

        var sorted = SubtitleOrder.Sort(outputs, new RipProfile());

        CollectionAssert.AreEqual(new[] { "clean", "sdh", "eng" }, sorted.Select(o => o.Name).ToArray());
    }
}
