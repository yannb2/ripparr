using Ripparr.Disc;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class WatchTracksTests
{
    private static readonly Mpls Film = Mpls.Parse(MplsBuilder.UhdFilm().Build());

    [TestMethod]
    public void TheJobsDefaultAudioAndFirstKeptSubtitleArePlayed()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        foreach (var track in plan.Tracks.Where(t => t.Kind == StreamKind.Audio))
        {
            track.Default = track.Pid == 0x1102;
        }

        var english = plan.Tracks.First(t => t.Kind == StreamKind.Subtitle && t.Pid == 0x1200);
        english.Keep = false;
        var kept = plan.Kept.First(t => t.Kind == StreamKind.Subtitle);

        var tracks = WatchTracks.From(plan);
        Assert.AreEqual(0x1102, tracks.Audio);
        Assert.AreEqual(kept.Pid, tracks.Subtitle);
        Assert.AreNotEqual(0x1200, tracks.Subtitle);
    }

    [TestMethod]
    public void AForcedDefaultSubtitleComesFirst()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        foreach (var track in plan.Tracks.Where(t => t.Kind == StreamKind.Subtitle))
        {
            track.Keep = true;
        }

        var last = plan.Tracks.Last(t => t.Kind == StreamKind.Subtitle);
        last.Forced = true;
        plan.SetDefault(last);
        Assert.AreEqual(last.Pid, WatchTracks.From(plan).Subtitle);
    }

    [TestMethod]
    public void NoSubtitleKeptMeansNoneBurnedIn()
    {
        var plan = TrackPlanner.Build(Film, new RipProfile(), "eng");
        foreach (var track in plan.Tracks.Where(t => t.Kind == StreamKind.Subtitle))
        {
            track.Keep = false;
        }

        Assert.IsNull(WatchTracks.From(plan).Subtitle);
        Assert.AreEqual(WatchTracks.None, WatchTracks.From(null));
    }

    [TestMethod]
    public void TheUrlCanOnlyAskForThePlaylistsOwnTracks()
    {
        Assert.AreEqual(new WatchTracks(0x1101, 0x1201), WatchTracks.Check(Film, 0x1101, 0x1201));
        // A subtitle PID given as audio, a PID the disc does not have: dropped.
        Assert.AreEqual(WatchTracks.None, WatchTracks.Check(Film, 0x1200, 0x1999));
        // A subtitle alone plays over the first audio track.
        Assert.AreEqual(new WatchTracks(0x1100, 0x1202), WatchTracks.Check(Film, null, 0x1202));
    }

    [TestMethod]
    public void EachChoiceHasItsOwnPreviewFile()
    {
        Assert.AreEqual("", WatchTracks.None.FileSuffix);
        Assert.AreEqual("-a4353-s4609", new WatchTracks(0x1101, 0x1201).FileSuffix);
        Assert.AreEqual("&audio=4353&subtitle=4609", new WatchTracks(0x1101, 0x1201).Query);
    }

    [TestMethod]
    public void ThePreviewMapsTheAudioByPidAndBurnsInTheSubtitle()
    {
        var arguments = string.Join(' ', Thumbnails.PreviewArguments("clip.m2ts", "12", "out.mp4", new WatchTracks(0x1101, 0x1201)));
        StringAssert.Contains(arguments, "-filter_complex [0:v:0]scale=640:-2[v];[0:i:4609]scale=640:-2[s];[v][s]overlay=eof_action=pass,format=yuv420p[out] -map [out]");
        StringAssert.Contains(arguments, "-map 0:i:4353");

        var plain = string.Join(' ', Thumbnails.PreviewArguments("clip.m2ts", "12", "out.mp4", WatchTracks.None));
        StringAssert.Contains(plain, "-map 0:v:0 -vf scale=640:-2,format=yuv420p -map 0:a:0?");
    }

    [TestMethod]
    public void TheLivePlayerKeepsVideoAudioAndSubtitleInThatOrder()
    {
        var tracks = new WatchTracks(0x1102, 0x1201);
        CollectionAssert.AreEqual(new[] { 0x1011, 0x1102, 0x1201 }, LivePlay.Streams(Film, tracks));
        Assert.IsNull(LivePlay.Streams(Film, WatchTracks.None));

        var arguments = string.Join(' ', LivePlay.Arguments("list.ffconcat", tracks));
        StringAssert.Contains(arguments, "[0:0]scale='min(1280,iw)':-2[v];[0:2]scale='min(1280,iw)':-2[s]");
        StringAssert.Contains(arguments, "-map [out] -map 0:1 ");

        var plain = string.Join(' ', LivePlay.Arguments("list.ffconcat"));
        StringAssert.Contains(plain, "-map 0:v:0 -vf scale='min(1280,iw)':-2,format=yuv420p -map 0:a:0?");
    }
}
