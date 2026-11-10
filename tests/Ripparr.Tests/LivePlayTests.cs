using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class LivePlayTests
{
    private static string Disc(params string[] clips)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ripparr-play-{Guid.NewGuid():N}");
        foreach (var clip in clips)
        {
            var path = Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }

        return root;
    }

    [TestMethod]
    public void TheListPlaysEachClipFromItsInTimeToItsOutTime()
    {
        var disc = Disc("00002", "00003");
        try
        {
            var playlist = Mpls.Parse(MplsBuilder.Feature(("00002", 60), ("00003", 40)).Build());
            var list = LivePlay.ConcatList(playlist, disc)!;

            // The builder's items start at 27 000 000 ticks of the 45 kHz clock: 600 s, the clip's own time stamps.
            var stream = Path.Combine(disc, "BDMV", "STREAM");
            Assert.AreEqual(
                "ffconcat version 1.0\n" +
                $"file '{Path.Combine(stream, "00002.m2ts")}'\ninpoint 600\noutpoint 660\n" +
                $"file '{Path.Combine(stream, "00003.m2ts")}'\ninpoint 600\noutpoint 640\n",
                list);
        }
        finally
        {
            Directory.Delete(disc, recursive: true);
        }
    }

    [TestMethod]
    public void StartingLaterSkipsWholeClipsAndCutsIntoTheOneItFallsIn()
    {
        var disc = Disc("00002", "00003");
        try
        {
            var playlist = Mpls.Parse(MplsBuilder.Feature(("00002", 60), ("00003", 40)).Build());
            var list = LivePlay.ConcatList(playlist, disc, fromSeconds: 75)!;

            StringAssert.DoesNotMatch(list, new System.Text.RegularExpressions.Regex("00002"));
            StringAssert.Contains(list, "inpoint 615\noutpoint 640\n");
        }
        finally
        {
            Directory.Delete(disc, recursive: true);
        }
    }

    [TestMethod]
    public void AMissingClipMeansNoList()
    {
        var disc = Disc("00002");
        try
        {
            var playlist = Mpls.Parse(MplsBuilder.Feature(("00002", 60), ("00003", 40)).Build());
            Assert.IsNull(LivePlay.ConcatList(playlist, disc));
        }
        finally
        {
            Directory.Delete(disc, recursive: true);
        }
    }

    [TestMethod]
    public void QuotesInTheClipPathAreEscaped()
    {
        var disc = Disc("00002");
        var quoted = disc + "-it's";
        Directory.Move(disc, quoted);
        try
        {
            var playlist = Mpls.Parse(MplsBuilder.Feature(("00002", 60)).Build());
            StringAssert.Contains(LivePlay.ConcatList(playlist, quoted), "-it'\\''s");
        }
        finally
        {
            Directory.Delete(quoted, recursive: true);
        }
    }

    [TestMethod]
    public void TheJobsTracksAreAskedForByPidBeforeTheClips()
    {
        var disc = Disc("00002");
        try
        {
            var playlist = Mpls.Parse(MplsBuilder.Feature(("00002", 60)).Build());
            var list = LivePlay.ConcatList(playlist, disc, streams: [0x1011, 0x1101, 0x1201])!;
            StringAssert.StartsWith(list,
                "ffconcat version 1.0\nstream\nexact_stream_id 0x1011\nstream\nexact_stream_id 0x1101\nstream\nexact_stream_id 0x1201\nfile '");
        }
        finally
        {
            Directory.Delete(disc, recursive: true);
        }
    }
}
