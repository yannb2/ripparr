using System.Buffers.Binary;
using System.Text;
using Ripparr.Disc;

namespace Ripparr.Tests;

// Discs whose BD-J menu builds the film's playlist at play time (The Fabelmans UHD: 4 short
// playlists, the film one 82 GB clip none of them plays).
[TestClass]
public class ClipPlaylistTests
{
    private const uint Start = 27_000_000;
    private const double FilmSeconds = 2 * 3600 + 31 * 60;

    /// <summary>A clip info file: one ATC with one STC sequence, then one program with these streams.</summary>
    internal static byte[] Clpi(double seconds, params (int Pid, byte[] Info)[] streams)
    {
        var sequence = new List<byte>();
        Put32(sequence, 0); // length, filled below
        sequence.Add(0);
        sequence.Add(1); // ATC sequences
        Put32(sequence, 0); // SPN
        sequence.Add(1); // STC sequences
        sequence.Add(0); // offset
        Put16(sequence, 0x1001); // PCR PID
        Put32(sequence, 0);
        Put32(sequence, Start);
        Put32(sequence, Start + (uint)(seconds * 45000));

        var program = new List<byte>();
        Put32(program, 0);
        program.Add(0);
        program.Add(1); // programs
        Put32(program, 0); // SPN
        Put16(program, 0x0100); // PMT PID
        program.Add((byte)streams.Length);
        program.Add(0); // groups
        foreach (var (pid, info) in streams)
        {
            Put16(program, pid);
            program.Add((byte)info.Length);
            program.AddRange(info);
        }

        var header = new List<byte>();
        header.AddRange(Encoding.ASCII.GetBytes("HDMV0300"));
        Put32(header, 40);
        Put32(header, 40 + sequence.Count);
        header.AddRange(new byte[40 - header.Count]);
        return [..header, ..sequence, ..program];
    }

    private static byte[] Lang(string code) => Encoding.ASCII.GetBytes(code);

    /// <summary>The Fabelmans' film clip: HEVC HDR10 with a DV layer, TrueHD and AC-3 English, French AC-3, English PGS, a menu.</summary>
    internal static byte[] FilmClpi() => Clpi(FilmSeconds,
        (0x1011, [0x24, 0x81, 0x30, 0x12, 0x80]),
        (0x1015, [0x24, 0x81, 0x30, 0x12, 0x00]),
        (0x1100, [0x83, 0x61, ..Lang("eng")]),
        (0x1101, [0x81, 0x61, ..Lang("eng")]),
        (0x1102, [0x81, 0x61, ..Lang("fra")]),
        (0x1200, [0x90, ..Lang("eng"), 0]),
        (0x1400, [0x91, ..Lang("eng"), 0]));

    [TestMethod]
    public void ClpiGivesTheLengthAndStreams()
    {
        var clpi = Ripparr.Disc.Clpi.Parse(FilmClpi());

        Assert.AreEqual("0300", clpi.Version);
        Assert.AreEqual(FilmSeconds, clpi.Duration.TotalSeconds, 0.01);
        Assert.AreEqual(6, clpi.Streams.Count, "the menu (IG) is left out");
        var video = clpi.Streams[0];
        Assert.AreEqual((byte)8, video.Format);
        Assert.AreEqual((byte)1, video.DynamicRange);
        Assert.AreEqual((byte)2, video.ColorSpace);
        Assert.AreEqual("fra", clpi.Streams[4].Language);
        Assert.AreEqual("eng", clpi.Streams[5].Language);
    }

    [TestMethod]
    public void ThePlaylistMadeFromItReadsLikeTheDiscsOwn()
    {
        var mpls = Mpls.Parse(Ripparr.Disc.Clpi.Parse(FilmClpi()).ToPlaylist("00294").Build());

        CollectionAssert.AreEqual(new[] { "00294" }, mpls.Clips.ToArray());
        Assert.AreEqual(FilmSeconds, mpls.Duration.TotalSeconds, 0.01);
        Assert.AreEqual(0, mpls.Chapters.Count);
        var video = mpls.Streams.Single(s => s.Kind == StreamKind.Video && !s.DolbyVisionEnhancementLayer);
        Assert.AreEqual(0x1011, video.Pid);
        Assert.AreEqual((byte)1, video.DynamicRange);
        Assert.AreEqual(0x1015, mpls.Streams.Single(s => s.DolbyVisionEnhancementLayer).Pid);
        CollectionAssert.AreEqual(new[] { "eng", "eng", "fra" }, mpls.Streams.Where(s => s.Kind == StreamKind.Audio).Select(s => s.Language).ToArray());
        Assert.AreEqual("eng", mpls.Streams.Single(s => s.Kind == StreamKind.Subtitle).Language);
    }

    [TestMethod]
    public void ABigClipNoPlaylistPlaysGetsItsOwnAndIsTheFilm()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ripparr-clpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "00294.clpi"), FilmClpi());
            File.WriteAllBytes(Path.Combine(folder, "00010.clpi"), FilmClpi());
            var disc = new DiscAnalysis
            {
                Source = new DiscSource("/x", SourceKind.Folder, "/", 0, DateTime.UtcNow),
                Playlists =
                [
                    new PlaylistInfo { Name = "00965", Mpls = Mpls.Parse(MplsBuilder.Feature(("00001", 333)).Build()) },
                    new PlaylistInfo { Name = "00149", Mpls = Mpls.Parse(MplsBuilder.Feature(("00002", 15_026)).Build()) }
                ],
                ClipSizes = new Dictionary<string, long>
                {
                    ["00294"] = 81_852_678_144, ["00001"] = 246_000_000, ["00002"] = 540_000_000,
                    ["00010"] = 500_000_000 // under 1 GB: left alone
                }
            };

            DiscAnalyzer.AddClipPlaylists(disc, folder);
            disc.Classify(TimeSpan.FromMinutes(2));

            Assert.AreEqual(3, disc.Playlists.Count);
            var film = disc.MainFeature!;
            Assert.AreEqual("clip00294", film.Name);
            Assert.AreEqual("Clip 00294", film.Label);
            Assert.AreEqual("00294", film.FromClip);
            Assert.IsNotNull(film.MadePlaylist);
            Assert.AreEqual(81_852_678_144, film.Size);
            StringAssert.Contains(film.MainReason, "No playlist on the disc plays this clip");
            Assert.IsFalse(disc.FilmMissing);
            Assert.IsTrue(disc.Find("00149")!.TooLittleData);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void AFilmPlaylistHoldingASliverOfTheDiscOffersAskMakeMkv()
    {
        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x", SourceKind.Folder, "/", 0, DateTime.UtcNow),
            Playlists = [new PlaylistInfo { Name = "00965", Mpls = Mpls.Parse(MplsBuilder.Feature(("00001", 333)).Build()) }],
            ClipSizes = new Dictionary<string, long> { ["00294"] = 81_852_678_144, ["00001"] = 246_000_000 }
        };
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.AreEqual("00965", disc.MainFeature?.Name);
        Assert.IsTrue(disc.FilmMissing);
        Assert.IsTrue(disc.NeedsFinding, "Check clip joins and Ask MakeMKV are shown");
    }

    [TestMethod]
    public void APlaylistClaimingSecondsButHoldingGigabytesHasFakedTimes()
    {
        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x", SourceKind.Folder, "/", 0, DateTime.UtcNow),
            Playlists =
            [
                new PlaylistInfo { Name = "00800", Mpls = Mpls.Parse(MplsBuilder.Feature(("00055", 30)).Build()) },
                new PlaylistInfo { Name = "00801", Mpls = Mpls.Parse(MplsBuilder.Feature(("00056", 5400)).Build()) },
                new PlaylistInfo { Name = "00001", Mpls = Mpls.Parse(MplsBuilder.Feature(("00001", 20)).Build()) }
            ],
            ClipSizes = new Dictionary<string, long> { ["00055"] = 60_000_000_000, ["00056"] = 9_000_000_000, ["00001"] = 50_000_000 }
        };
        disc.Classify(TimeSpan.FromMinutes(2));

        var faked = disc.Find("00800")!;
        Assert.IsTrue(faked.LengthLooksFaked);
        Assert.IsFalse(faked.Short);
        Assert.IsTrue(faked.MainFeature, "gigabytes beat a length that cannot be true");
        Assert.IsTrue(disc.Find("00001")!.Short, "a small short playlist stays short");
        Assert.IsTrue(disc.NeedsFinding);
    }

    [TestMethod]
    public void MakeMkvsPickWinsEvenWhenItLooksShort()
    {
        var disc = new DiscAnalysis
        {
            Source = new DiscSource("/x", SourceKind.Folder, "/", 0, DateTime.UtcNow),
            Playlists =
            [
                new PlaylistInfo { Name = "00800", Mpls = Mpls.Parse(MplsBuilder.Feature(("00055", 5400)).Build()) },
                new PlaylistInfo { Name = "00900", Mpls = Mpls.Parse(MplsBuilder.Feature(("00056", 60)).Build()) }
            ],
            ClipSizes = new Dictionary<string, long> { ["00055"] = 30_000_000_000, ["00056"] = 1_000_000_000 },
            MakeMkvPick = "00900"
        };
        disc.Classify(TimeSpan.FromMinutes(2));

        Assert.AreEqual("00900", disc.MainFeature?.Name);
        Assert.IsFalse(disc.Find("00900")!.Short);
        Assert.AreEqual("MakeMKV calls it the main feature.", disc.MainFeature!.MainReason);
    }

    private static void Put16(List<byte> list, int value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        list.AddRange(buffer);
    }

    private static void Put32(List<byte> list, long value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
        list.AddRange(buffer);
    }
}
