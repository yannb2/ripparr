using System.Buffers.Binary;
using Ripparr.Disc;
using Arrkit.Subtitles;

namespace Ripparr.Tests;

[TestClass]
public class PgsExtractorTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "Samples", "captions.sup");

    /// <summary>
    /// The sample .sup as a Blu-ray would carry it: each segment in a PES packet with its PTS,
    /// cut into 192-byte transport packets on one PID, video packets of another PID in between.
    /// </summary>
    private static byte[] AsTransportStream(byte[] sup, int pid, long ptsShift = 0)
    {
        var packets = new List<byte>();
        var counter = 0;
        for (var at = 0; at + 13 <= sup.Length;)
        {
            var pts = BinaryPrimitives.ReadUInt32BigEndian(sup.AsSpan(at + 2)) + ptsShift;
            var size = BinaryPrimitives.ReadUInt16BigEndian(sup.AsSpan(at + 11));
            var segment = sup.AsSpan(at + 10, 3 + size);
            at += 13 + size;

            var pes = new List<byte> { 0, 0, 1, 0xBD };
            var length = 3 + 5 + segment.Length;
            pes.Add((byte)(length >> 8));
            pes.Add((byte)length);
            pes.AddRange(new byte[] { 0x81, 0x80, 5 });
            pes.Add((byte)(0x21 | ((pts >> 29) & 0x0E)));
            pes.Add((byte)(pts >> 22));
            pes.Add((byte)(0x01 | ((pts >> 14) & 0xFE)));
            pes.Add((byte)(pts >> 7));
            pes.Add((byte)(0x01 | ((pts << 1) & 0xFE)));
            pes.AddRange(segment.ToArray());

            for (var offset = 0; offset < pes.Count; offset += 184)
            {
                packets.AddRange(Packet(0x1011, false, []));
                var chunk = pes.Skip(offset).Take(184).ToList();
                while (chunk.Count < 184)
                {
                    chunk.Add(0xFF);
                }

                packets.AddRange(Packet(pid, offset == 0, chunk, counter++));
            }
        }

        return packets.ToArray();
    }

    private static byte[] Packet(int pid, bool start, List<byte> payload, int counter = 0)
    {
        var packet = new byte[192];
        packet[4] = 0x47;
        packet[5] = (byte)((start ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        packet[6] = (byte)pid;
        packet[7] = (byte)(0x10 | (counter & 0x0F));
        payload.CopyTo(packet, 8);
        return packet;
    }

    [TestMethod]
    public void PullsASubtitleTrackOutOfTheStream()
    {
        var original = PgsReader.Read(Sample);
        using var output = new MemoryStream();
        using (var extractor = new PgsExtractor(new Dictionary<int, Stream> { [0x1200] = output }))
        {
            extractor.StartItem(0, 0, uint.MaxValue);
            extractor.Feed(AsTransportStream(File.ReadAllBytes(Sample), 0x1200));
            extractor.Finish();
        }

        var captions = PgsReader.Read(output.ToArray());
        Assert.HasCount(original.Count, captions);
        CollectionAssert.AreEqual(original.Select(c => c.Start).ToArray(), captions.Select(c => c.Start).ToArray());
        CollectionAssert.AreEqual(original.Select(c => c.Forced).ToArray(), captions.Select(c => c.Forced).ToArray());
    }

    [TestMethod]
    public void PutsTimesOnThePlaylistClock()
    {
        // The clip starts at 600 s (its in-time) and is the second play item, 100 s into the playlist.
        var stream = AsTransportStream(File.ReadAllBytes(Sample), 0x1200, ptsShift: 600 * 90000L);
        using var output = new MemoryStream();
        using (var extractor = new PgsExtractor(new Dictionary<int, Stream> { [0x1200] = output }))
        {
            extractor.StartItem(100 * 45000L, 600 * 45000, 700 * 45000);
            extractor.Feed(stream);
            extractor.Finish();
        }

        var first = PgsReader.Read(output.ToArray())[0];
        Assert.AreEqual(TimeSpan.FromSeconds(101), first.Start);
    }

    [TestMethod]
    public void LeavesOutWhatTheItemDoesNotPlay()
    {
        var stream = AsTransportStream(File.ReadAllBytes(Sample), 0x1200);
        using var output = new MemoryStream();
        using (var extractor = new PgsExtractor(new Dictionary<int, Stream> { [0x1200] = output }))
        {
            extractor.StartItem(0, 2000 * 45000, 3000 * 45000);
            extractor.Feed(stream);
            extractor.Finish();
        }

        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public void ReadsAPesTimestamp()
    {
        // 0x1_2345_6789 in the PES layout: 0010 xxx1, 8 bits, 7 bits + 1, 8 bits, 7 bits + 1.
        var pts = 0x1_2345_6789L;
        byte[] bytes =
        [
            (byte)(0x21 | ((pts >> 29) & 0x0E)), (byte)(pts >> 22), (byte)(0x01 | ((pts >> 14) & 0xFE)),
            (byte)(pts >> 7), (byte)(0x01 | ((pts << 1) & 0xFE))
        ];
        Assert.AreEqual(pts, PgsExtractor.Timestamp(bytes));
    }
}
