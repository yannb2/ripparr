using Microsoft.Extensions.Logging.Abstractions;
using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Options;
using Ripparr.Tools;

namespace Ripparr.Tests;

/// <summary>Finding the film on discs that hide it among fake playlists.</summary>
[TestClass]
public class PlaylistFinderTests
{
    private static PlaylistInfo Playlist(string name, MplsBuilder builder) =>
        new() { Name = name, Mpls = Mpls.Parse(builder.Build()) };

    private static DiscAnalysis Disc(Dictionary<string, long> sizes, params PlaylistInfo[] playlists) =>
        new()
        {
            Source = new DiscSource("/x", SourceKind.Folder, "/", 0, DateTime.UtcNow),
            Playlists = playlists.ToList(),
            ClipSizes = sizes
        };

    [TestMethod]
    public void ALongPlaylistWithTooLittleDataIsNotTheFilm()
    {
        // The Fabelmans UHD: a 4 h playlist looping 251 tiny clips (540 MB) and the real 2.5 h film.
        var loop = Enumerable.Range(0, 251).Select(i => ($"{100 + i % 4:00000}", 60.0)).ToArray();
        var sizes = new Dictionary<string, long>
        {
            ["00100"] = 135_000_000, ["00101"] = 135_000_000, ["00102"] = 135_000_000, ["00103"] = 135_000_000,
            ["00800"] = 70_000_000_000
        };
        var disc = Disc(sizes,
            Playlist("00149", MplsBuilder.Feature(loop)),
            Playlist("00800", MplsBuilder.Feature(("00800", 9060))));

        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsTrue(disc.Find("00149")!.TooLittleData);
        Assert.AreEqual("00800", disc.MainFeature?.Name);
        StringAssert.Contains(disc.MainFeature!.MainReason, "too little data");
        Assert.IsFalse(disc.Find("00800")!.TooLittleData);
    }

    [TestMethod]
    public void UnknownClipSizesAreNotTooLittleData()
    {
        var disc = Disc(new Dictionary<string, long> { ["00001"] = 1 },
            Playlist("00800", MplsBuilder.Feature(("00055", 6000))));
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsFalse(disc.Find("00800")!.TooLittleData);
        Assert.AreEqual("00800", disc.MainFeature?.Name);
    }

    [TestMethod]
    public void ShuffledCopiesOfTheFilmAreLookalikes()
    {
        var sizes = new Dictionary<string, long> { ["00001"] = 20_000_000_000, ["00002"] = 20_000_000_000, ["00003"] = 20_000_000_000, ["00004"] = 1_000_000_000 };
        var disc = Disc(sizes,
            Playlist("00800", MplsBuilder.Feature(("00001", 2000), ("00002", 2000), ("00003", 2000))),
            Playlist("00801", MplsBuilder.Feature(("00002", 2000), ("00001", 2000), ("00003", 2000))),
            Playlist("00802", MplsBuilder.Feature(("00003", 2000), ("00002", 2000), ("00001", 2000))),
            Playlist("00803", MplsBuilder.Feature(("00004", 600))));

        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.IsTrue(disc.HidesFilm);
        Assert.IsTrue(new[] { "00800", "00801", "00802" }.All(n => disc.Find(n)!.Lookalike));
        Assert.IsFalse(disc.Find("00803")!.Lookalike);
    }

    [TestMethod]
    public void MakeMkvsPickAndAClearJoinWinnerBeatLength()
    {
        var sizes = new Dictionary<string, long> { ["00001"] = 20_000_000_000, ["00002"] = 20_000_000_000, ["00003"] = 20_000_000_000 };
        var disc = Disc(sizes,
            Playlist("00800", MplsBuilder.Feature(("00001", 2000), ("00002", 2000), ("00003", 2010))),
            Playlist("00801", MplsBuilder.Feature(("00002", 2000), ("00001", 2000), ("00003", 2000))),
            Playlist("00802", MplsBuilder.Feature(("00003", 2000), ("00002", 2000), ("00001", 2000))));
        disc.Classify(TimeSpan.FromMinutes(2));
        Assert.AreEqual("00800", disc.MainFeature?.Name, "longest first");

        disc.JoinPick = "00802";
        disc.Classify(TimeSpan.FromMinutes(2));
        Assert.AreEqual("00802", disc.MainFeature?.Name);
        StringAssert.Contains(disc.MainFeature!.MainReason, "join");

        disc.MakeMkvPick = "00801";
        disc.Classify(TimeSpan.FromMinutes(2));
        Assert.AreEqual("00801", disc.MainFeature?.Name, "MakeMKV's answer comes first");
        StringAssert.Contains(disc.MainFeature!.MainReason, "MakeMKV");
    }

    [TestMethod]
    public void ScoresJoinsAndNamesOnlyAClearWinner()
    {
        byte[] Grey(byte value) => Enumerable.Repeat(value, PlaylistFinder.FrameWidth * PlaylistFinder.FrameHeight).ToArray();
        // Clip n starts at grey 10n and ends at 10n+9: in order, each join changes by 1.
        var frames = Enumerable.Range(1, 3).ToDictionary(n => $"0000{n}", n => (Grey((byte)(10 * n)), Grey((byte)(10 * n + 9))));

        Assert.AreEqual(1.0, PlaylistFinder.Score(["00001", "00002", "00003"], frames));
        Assert.AreEqual(19.0, PlaylistFinder.Score(["00003", "00002", "00001"], frames)!.Value, 0.001);
        Assert.IsNull(PlaylistFinder.Score(["00001"], frames));
        Assert.IsNull(PlaylistFinder.Score(["00001", "00009"], frames));

        Assert.AreEqual("a", PlaylistFinder.Winner(new Dictionary<string, double> { ["a"] = 1, ["b"] = 19 }));
        Assert.IsNull(PlaylistFinder.Winner(new Dictionary<string, double> { ["a"] = 10, ["b"] = 11 }));
        Assert.AreEqual(0.0, PlaylistFinder.Difference(Grey(5), Grey(5)));
    }

    [TestMethod]
    public void ReadsMakeMkvsMainFeature()
    {
        const string output = """
            MSG:1005,0,1,"MakeMKV v1.18.4 linux(x64-release) started","%1 started","MakeMKV v1.18.4 linux(x64-release)"
            TINFO:0,16,0,"00149.mpls"
            TINFO:0,9,0,"4:10:26"
            TINFO:1,16,0,"00800.mpls"
            TINFO:1,49,0,"FPL_MainFeature"
            TINFO:2,16,0,"00030.mpls"
            """;

        Assert.AreEqual("00800", PlaylistFinder.MainFeatureOf(output));
        Assert.IsNull(PlaylistFinder.MainFeatureOf("TINFO:0,16,0,\"00149.mpls\""));
    }

    [TestMethod]
    public async Task FindsTheFilmByItsSeamlessJoins()
    {
        var available = await ProcessRunner.RunAsync("ffmpeg", ["-version"], TimeSpan.FromSeconds(10));
        if (!available.Success)
        {
            Assert.Inconclusive("ffmpeg is not installed here (it is in the image).");
        }

        var folder = Path.Combine(Path.GetTempPath(), "ripparr-joins-" + Guid.NewGuid().ToString("N"));
        var stream = Path.Combine(folder, "BDMV", "STREAM");
        Directory.CreateDirectory(stream);
        try
        {
            // One continuous shot (a brightness ramp) cut into three clips, a key frame every half second as on discs.
            for (var n = 0; n < 3; n++)
            {
                var result = await ProcessRunner.RunAsync("ffmpeg",
                [
                    "-v", "error", "-y", "-f", "lavfi",
                    "-i", $"color=c=gray:s=320x180:r=24:d=4,geq=lum='clip(({n}*4+T)*20,0,255)':cb=128:cr=128",
                    "-c:v", "libx264", "-preset", "ultrafast", "-g", "12", "-pix_fmt", "yuv420p", "-f", "mpegts",
                    Path.Combine(stream, $"0000{n + 1}.m2ts")
                ], TimeSpan.FromMinutes(1));
                Assert.IsTrue(result.Success, result.Error);
            }

            var sizes = Enumerable.Range(1, 3).ToDictionary(n => $"0000{n}", _ => 20_000_000_000L);
            var disc = new DiscAnalysis
            {
                Source = new DiscSource(folder, SourceKind.Folder, folder, 0, DateTime.UtcNow),
                ClipSizes = sizes,
                Playlists =
                [
                    Playlist("00800", MplsBuilder.Feature(("00003", 2000), ("00001", 2000), ("00002", 2000))),
                    Playlist("00801", MplsBuilder.Feature(("00001", 2000), ("00002", 2000), ("00003", 2000))),
                    Playlist("00802", MplsBuilder.Feature(("00002", 2000), ("00003", 2000), ("00001", 2000)))
                ]
            };
            disc.Classify(TimeSpan.FromMinutes(2));
            var options = Microsoft.Extensions.Options.Options.Create(new RipparrOptions { MinPlaylistSeconds = 120 });
            var finder = new PlaylistFinder(options, new JobStore(Path.Combine(folder, "jobs.db")), NullLogger<PlaylistFinder>.Instance);

            var message = await finder.CheckJoinsAsync(disc, _ => { }, CancellationToken.None);

            Assert.AreEqual("00801", disc.JoinPick, message);
            Assert.AreEqual("00801", disc.MainFeature?.Name);
            Assert.IsTrue(disc.Find("00801")!.JoinScore < disc.Find("00800")!.JoinScore);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
