using Ripparr.Disc;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class SubtitleFlagsTests
{
    private static readonly Mpls Film = Mpls.Parse(MplsBuilder.UhdFilm().Build());

    private static SubtitleScan Scan(int version, bool full, params ScannedSubtitle[] tracks) => new()
    {
        Key = "k", Quick = !full, FullText = full, GuessVersion = version,
        Tracks = tracks.ToDictionary(t => t.Pid)
    };

    [TestMethod]
    public void TicksWhatTheReadFoundOnceAndKeepsHandChanges()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        var state = new PlaylistTracksState();
        SubtitleFlags.Apply(plan, state, Scan(1, false, new ScannedSubtitle(0x1200, 40, 0, Sdh: true), new ScannedSubtitle(0x1201, 30, 0, Commentary: true)), detectSdh: true);

        var english = plan.Tracks.First(t => t.Pid == 0x1200);
        var french = plan.Tracks.First(t => t.Pid == 0x1201);
        Assert.IsTrue(english.HearingImpaired);
        Assert.IsTrue(french.Commentary);
        Assert.AreEqual("40 elements in the sample", english.Details);

        // Unticked by hand: the same read applied again does not tick it back.
        english.HearingImpaired = false;
        SubtitleFlags.Apply(plan, state, Scan(1, false, new ScannedSubtitle(0x1200, 40, 0, Sdh: true)), detectSdh: true);
        Assert.IsFalse(english.HearingImpaired);
    }

    [TestMethod]
    public void LeavesSdhAloneWhenTheProfileDoesNotDetectIt()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        SubtitleFlags.Apply(plan, new PlaylistTracksState(), Scan(1, true, new ScannedSubtitle(0x1200, 40, 3, Sdh: true)), detectSdh: false);
        Assert.IsFalse(plan.Tracks.First(t => t.Pid == 0x1200).HearingImpaired);
        Assert.AreEqual("40 elements, 3 forced", plan.Tracks.First(t => t.Pid == 0x1200).Details);
    }
}
