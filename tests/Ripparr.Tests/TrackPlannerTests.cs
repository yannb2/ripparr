using Ripparr.Disc;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class TrackPlannerTests
{
    private static readonly Mpls Film = Mpls.Parse(MplsBuilder.UhdFilm().Build());

    [TestMethod]
    public void KeepsVideoAndTheDolbyVisionLayer()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        var video = plan.Tracks.Where(t => t.Kind == StreamKind.Video).ToList();

        Assert.IsTrue(video.All(v => v.Keep));
        Assert.IsTrue(video[0].Default);
        Assert.IsTrue(plan.MergeDolbyVisionLayer);
        Assert.AreEqual("eng", video[0].Language, "video language = original");
        Assert.AreEqual(1, plan.Kept.Count(t => t.Kind == StreamKind.Video), "the layer is never its own track");
    }

    [TestMethod]
    public void DropsTheLayerWhenTheProfileSaysSo()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile { KeepDolbyVisionLayer = false }, "eng");
        Assert.IsFalse(plan.MergeDolbyVisionLayer);
    }

    [TestMethod]
    public void AudioKeepsLosslessAndCommentaryInTheProfilesOrder()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        var audio = plan.Tracks.Where(t => t.Kind == StreamKind.Audio).ToList();

        // TrueHD eng, DTS-HD MA fra, then the commentary; the English AC-3 5.1 is a lossy duplicate.
        var kept = audio.Where(t => t.Keep).Select(t => $"{t.Codec} {t.Language}{(t.Commentary ? " commentary" : "")}").ToArray();
        CollectionAssert.AreEqual(new[] { "TrueHD eng", "DTS-HD MA fra", "AC3 eng commentary" }, kept);
        Assert.IsTrue(audio.Single(t => t.Codec == "TrueHD").Default);
        Assert.AreEqual(1, audio.Count(t => t.Default));
        Assert.IsFalse(audio.Single(t => t.Codec == "AC3" && !t.Commentary).Keep);
    }

    [TestMethod]
    public void OriginalLanguageComesFirst()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "fre");
        var first = plan.Kept.First(t => t.Kind == StreamKind.Audio);

        Assert.AreEqual("fra", first.Language, "fre and fra are the same language");
        Assert.IsTrue(first.Default);
    }

    [TestMethod]
    public void SubtitlesFollowTheListAndAreNeverDefault()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        var subtitles = plan.Tracks.Where(t => t.Kind == StreamKind.Subtitle).ToList();

        CollectionAssert.AreEqual(new[] { "eng", "fra" }, subtitles.Where(s => s.Keep).Select(s => s.Language).ToArray());
        Assert.IsFalse(subtitles.Any(s => s.Default));
        Assert.IsFalse(subtitles.Single(s => s.Language == "spa").Keep);
    }

    [TestMethod]
    public void MovingStaysWithinAKind()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        var firstAudio = plan.Tracks.First(t => t.Kind == StreamKind.Audio);
        var index = plan.Tracks.IndexOf(firstAudio);

        plan.Move(firstAudio, -1);
        Assert.AreEqual(index, plan.Tracks.IndexOf(firstAudio), "can't move above the video");

        plan.Move(firstAudio, 1);
        Assert.AreEqual(index + 1, plan.Tracks.IndexOf(firstAudio));
    }
}
