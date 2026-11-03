using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class StreamProbeTests
{
    /// <summary>192-byte Blu-ray packets: a 4-byte header, then a TS packet with its sync byte and PID.</summary>
    private static byte[] Packets(params (int Pid, int Count)[] pids)
    {
        var data = new List<byte>();
        foreach (var (pid, count) in pids)
        {
            for (var i = 0; i < count; i++)
            {
                var packet = new byte[192];
                packet[4] = 0x47;
                packet[5] = (byte)((pid >> 8) & 0x1F);
                packet[6] = (byte)(pid & 0xFF);
                data.AddRange(packet);
            }
        }

        return data.ToArray();
    }

    [TestMethod]
    public void CountsBytesPerPid()
    {
        var counts = new Dictionary<int, long>();
        StreamProbe.Count(Packets((0x1011, 30), (0x1100, 2)), counts);
        Assert.AreEqual(30 * 192, counts[0x1011]);
        Assert.AreEqual(2 * 192, counts[0x1100]);
    }

    [TestMethod]
    public async Task SamplesAStreamInWholeUnits()
    {
        // 64 aligned units of 32 packets: three quarters video, one quarter audio, interleaved.
        var unit = Packets((0x1011, 24), (0x1100, 8));
        var data = Enumerable.Repeat(unit, 400).SelectMany(u => u).ToArray();
        using var stream = new MemoryStream(data);
        var counts = await StreamProbe.CountAsync(stream, data.Length, 2, CancellationToken.None);
        Assert.AreEqual(3.0, counts[0x1011] / (double)counts[0x1100], 0.01);
    }

    [TestMethod]
    public void SharesTheClipSizeOutByPid()
    {
        var playlist = new PlaylistInfo { Name = "00800", Mpls = Mpls.Parse(MplsBuilder.UhdFilm(100).Build()) };
        var facts = new List<StreamFacts> { new() { Kind = StreamKind.Audio, Id = 0x1100, Format = "MLP FBA", Channels = 8 } };
        var bytes = new Dictionary<int, long> { [0x1011] = 900, [0x1100] = 100 };
        var result = StreamProbe.Build(playlist, facts, bytes, 60_000_000_000, estimated: false);

        // 6000 s; the payload is 184 of each 192 bytes.
        Assert.AreEqual(54_000_000_000 * 184 / 192, result.Streams[0x1011].Size);
        Assert.AreEqual(6_000_000_000 * 184 / 192, result.Streams[0x1100].Size);
        Assert.AreEqual(6_000_000_000L * 184 / 192 * 8 / 6000, result.Streams[0x1100].BitRate);
        Assert.AreEqual(8, result.Streams[0x1100].Facts!.Channels);
        Assert.IsNull(result.Streams[0x1101].Facts);
        Assert.AreEqual(0, result.Streams[0x1101].Size);

        // A constant bit rate is taken as it is.
        facts.Add(new StreamFacts { Kind = StreamKind.Audio, Id = 0x1101, Format = "AC-3", BitRate = 640_000, BitRateMode = "CBR" });
        result = StreamProbe.Build(playlist, facts, bytes, 60_000_000_000, estimated: false);
        Assert.AreEqual(640_000L * 6000 / 8, result.Streams[0x1101].Size);
        Assert.AreEqual(640_000, result.Streams[0x1101].BitRate);
    }

    [TestMethod]
    public void ReadsHdrFromThePlaylist()
    {
        var builder = MplsBuilder.Feature(("00001", 6000));
        builder.Video.Add(new MplsBuilder.Stream(0x24, 0x1011, Format: 8, Rate: 1, Hdr: 0x12, HdrFlags: 0x40));
        builder.Video.Add(new MplsBuilder.Stream(0x1B, 0x1012, Format: 6, Rate: 1));
        var streams = Mpls.Parse(builder.Build()).Streams;

        Assert.AreEqual(1, streams[0].DynamicRange);
        Assert.AreEqual(2, streams[0].ColorSpace);
        Assert.IsTrue(streams[0].Hdr10Plus);
        Assert.AreEqual("2160p · 10-bit · HDR10+ · 23.976 fps", StreamCoding.Details(streams[0]));
        Assert.AreEqual("1080p · 8-bit · SDR · 23.976 fps", StreamCoding.Details(streams[1]));
    }

    [TestMethod]
    public void DescribesWhatTheStreamsSay()
    {
        var mpls = Mpls.Parse(MplsBuilder.UhdFilm().Build());
        var video = mpls.Streams[0];
        var baseLayer = new StreamFacts
        {
            Kind = StreamKind.Video, Format = "HEVC", Width = 3840, Height = 2160, BitDepth = 10, ChromaSubsampling = "4:2:0",
            FrameRate = "23.976", HdrFormat = "SMPTE ST 2086", HdrCompatibility = "HDR10"
        };
        Assert.AreEqual("3840x2160 · 10-bit · 4:2:0 · DV 7 HDR10 · 23.976 fps", StreamProbe.Describe(video, baseLayer, dolbyVisionLayer: true));
        Assert.AreEqual("3840x2160 · 10-bit · 4:2:0 · HDR10 · 23.976 fps", StreamProbe.Describe(video, baseLayer, dolbyVisionLayer: false));

        var audio = new StreamFacts { Kind = StreamKind.Audio, Format = "MLP FBA", SamplingRate = 48000, BitDepth = 24, Channels = 8 };
        Assert.AreEqual("48 kHz · 24-bit", StreamProbe.Describe(mpls.Streams[1], audio, true));

        var pgs = new StreamFacts { Kind = StreamKind.Subtitle, Format = "PGS", ElementCount = 1534 };
        Assert.AreEqual("1534 elements", StreamProbe.Describe(mpls.Streams[5], pgs, true));

        // Not read yet: what the playlist says.
        Assert.AreEqual("2160p · 10-bit · DV 7 SDR · 23.976 fps", StreamProbe.Describe(video, null, true));
    }

    [TestMethod]
    public void PutsTheDetailsOnTheJobTracks()
    {
        var mpls = Mpls.Parse(MplsBuilder.UhdFilm().Build());
        var playlist = new PlaylistInfo { Name = "00800", Mpls = mpls };
        var plan = TrackPlanner.Build(mpls, new RipProfile(), "eng");
        var facts = new List<StreamFacts> { new() { Kind = StreamKind.Audio, Id = 0x1100, Format = "MLP FBA", SamplingRate = 48000, BitDepth = 24 } };
        StreamProbe.Apply(plan, playlist, StreamProbe.Build(playlist, facts, new Dictionary<int, long>(), 0, false));
        Assert.AreEqual("48 kHz · 24-bit", plan.Tracks.Single(t => t.Pid == 0x1100).Details);
    }

    [TestMethod]
    public void ReadsThePidFromMediaInfo()
    {
        var facts = MediaInfoReader.Parse("""{"media":{"track":[{"@type":"Video","ID":"4113","Format":"HEVC"},{"@type":"Audio","ID":"4352-CC3","Format":"AC-3"}]}}""");
        CollectionAssert.AreEqual(new[] { 4113, 4352 }, facts.Select(f => f.Id).ToArray());
    }
}
