using System.IO.Compression;
using System.Text;
using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class MenuOrderTests
{
    private static byte[] Command(byte b0, byte b1, byte setOption, uint dst, uint src) =>
        [b0, b1, 0, setOption, (byte)(dst >> 24), (byte)(dst >> 16), (byte)(dst >> 8), (byte)dst, (byte)(src >> 24), (byte)(src >> 16), (byte)(src >> 8), (byte)src];

    // "Move GPR4075, n" then "JumpObject 1": the movie object plays the playlist in GPR4075.
    private static byte[][] MoveAndJump(uint playlist) => [Command(0x50, 0x40, 1, 4075, playlist), Command(0x21, 0x80, 0, 1, 0)];

    private static byte[][] PlayPl(uint playlist) => [Command(0x22, 0x80, 0, playlist, 0)];

    private sealed record Button(int Id, int Numeric, int X, int Y, byte[][] Commands);

    private static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }

    private static void Effects(List<byte> b, bool withOne)
    {
        if (!withOne)
        {
            b.Add(0); // windows
            b.Add(0); // effects
            return;
        }

        b.Add(1); b.Add(0); U16(b, 0); U16(b, 0); U16(b, 1920); U16(b, 1080); // one window
        b.Add(1); // one effect
        b.AddRange(new byte[] { 0, 0, 10 }); b.Add(0); // duration, palette
        b.Add(1); U16(b, 5); b.Add(0); b.Add(0x80); U16(b, 10); U16(b, 20); // a cropped object
        U16(b, 0); U16(b, 0); U16(b, 100); U16(b, 100);
    }

    private static byte[] Composition(params Button[][] pages)
    {
        var b = new List<byte> { 0x80, 0, 0, 0, (byte)pages.Length }; // stream model 1 (no PTS), user time-out
        for (var page = 0; page < pages.Length; page++)
        {
            b.Add((byte)page); b.Add(0); b.AddRange(new byte[8]);
            Effects(b, withOne: page == 0);
            Effects(b, withOne: false);
            b.Add(0); U16(b, 0); U16(b, 0); b.Add(0);
            b.Add(1); // one group
            U16(b, pages[page][0].Id);
            b.Add((byte)pages[page].Length);
            foreach (var button in pages[page])
            {
                U16(b, button.Id); U16(b, button.Numeric); b.Add(0); U16(b, button.X); U16(b, button.Y);
                b.AddRange(new byte[8]); b.AddRange(new byte[5]); b.AddRange(new byte[6]); b.AddRange(new byte[5]);
                U16(b, button.Commands.Length);
                foreach (var command in button.Commands)
                {
                    b.AddRange(command);
                }
            }
        }

        return b.ToArray();
    }

    // The composition in two ICS segments (first and last fragment), each in a PES packet, in 192-byte packets.
    private static byte[] Clip(byte[] composition, int pid = 0x1400)
    {
        var half = composition.Length / 2;
        var first = new List<byte> { 0x18 };
        var body = new List<byte>();
        body.AddRange(new byte[5]); body.AddRange(new byte[3]); body.Add(0x80);
        body.Add((byte)(composition.Length >> 16)); body.Add((byte)(composition.Length >> 8)); body.Add((byte)composition.Length);
        body.AddRange(composition.Take(half));
        U16(first, body.Count); first.AddRange(body);
        var second = new List<byte> { 0x18 };
        var rest = new List<byte>();
        rest.AddRange(new byte[5]); rest.AddRange(new byte[3]); rest.Add(0x40);
        rest.AddRange(composition.Skip(half));
        U16(second, rest.Count); second.AddRange(rest);

        var output = new List<byte>();
        // Some video packets first, then the menu.
        output.AddRange(Packets(0x1011, [0, 0, 1, 0xE0, 0, 0, 0x80, 0, 0, 1, 2, 3]));
        output.AddRange(Packets(pid, Pes(first)));
        output.AddRange(Packets(pid, Pes(second)));
        output.AddRange(Packets(pid, Pes([0x80, 0, 0]))); // end of display set
        return output.ToArray();
    }

    private static List<byte> Pes(List<byte> segment)
    {
        var pes = new List<byte> { 0, 0, 1, 0xBD };
        U16(pes, segment.Count + 3);
        pes.AddRange(new byte[] { 0x81, 0x00, 0x00 });
        pes.AddRange(segment);
        return pes;
    }

    private static IEnumerable<byte> Packets(int pid, List<byte> pes)
    {
        for (var at = 0; at < pes.Count; at += 184)
        {
            var chunk = pes.Skip(at).Take(184).ToList();
            var packet = new List<byte> { 0, 0, 0, 0, 0x47, (byte)((at == 0 ? 0x40 : 0) | (pid >> 8)), (byte)pid };
            if (chunk.Count == 184)
            {
                packet.Add(0x10);
            }
            else
            {
                packet.Add(0x30);
                var stuffing = 183 - chunk.Count;
                packet.Add((byte)stuffing);
                if (stuffing > 0)
                {
                    packet.Add(0);
                    packet.AddRange(Enumerable.Repeat((byte)0xFF, stuffing - 1));
                }
            }

            packet.AddRange(chunk);
            foreach (var b in packet)
            {
                yield return b;
            }
        }
    }

    private static readonly string[] Episodes = ["00117", "00118", "00152"];

    private static byte[] DoctorWhoMenu(bool numbered = false) => Clip(Composition(
        // Main menu: Play all, and one episode (the "next episode" button).
        [new Button(1, 0xFFFF, 100, 800, PlayPl(4)), new Button(2, 0xFFFF, 400, 800, MoveAndJump(118))],
        // Episodes page: top to bottom 152, 117, 118 (the remote's numbers say 3, 1, 2 when numbered).
        [
            new Button(10, numbered ? 3 : 0xFFFF, 200, 300, MoveAndJump(117)),
            new Button(11, numbered ? 1 : 0xFFFF, 200, 100, PlayPl(152)),
            new Button(12, numbered ? 2 : 0xFFFF, 200, 500, MoveAndJump(118)),
            new Button(13, numbered ? 4 : 0xFFFF, 900, 900, [Command(0x21, 0x80, 0, 0, 0)])
        ]));

    [TestMethod]
    public void ReadsTheMenuButtonsAndTheirCommands()
    {
        var buttons = IgMenu.Read(new MemoryStream(DoctorWhoMenu()), 1 << 20);

        Assert.HasCount(6, buttons);
        Assert.AreEqual(1, buttons.Single(b => b.Id == 11).Page);
        CollectionAssert.AreEqual(new uint[] { 117 }, buttons.Single(b => b.Id == 10).PlaylistNumbers.ToArray());
        CollectionAssert.AreEqual(new uint[] { 152 }, buttons.Single(b => b.Id == 11).PlaylistNumbers.ToArray());
    }

    [TestMethod]
    public void OrdersTheEpisodesAsTheMenuShowsThem()
    {
        var byPlace = IgMenu.Order(IgMenu.Read(new MemoryStream(DoctorWhoMenu()), 1 << 20), Episodes);
        CollectionAssert.AreEqual(new[] { "00152", "00117", "00118" }, byPlace, "the page with all three, top to bottom");

        var byNumber = IgMenu.Order(IgMenu.Read(new MemoryStream(DoctorWhoMenu(numbered: true)), 1 << 20), Episodes);
        CollectionAssert.AreEqual(new[] { "00152", "00118", "00117" }, byNumber, "the remote's numbers beat the places");

        Assert.IsNull(IgMenu.Order(IgMenu.Read(new MemoryStream(DoctorWhoMenu()), 1 << 20), ["00300", "00301"]));
        Assert.IsEmpty(IgMenu.Read(new MemoryStream(Clip(Composition([new Button(1, 0, 0, 0, PlayPl(5))]), pid: 0x1200)), 1 << 20),
            "a subtitle PID is no menu");
    }

    [TestMethod]
    public void ScansAJavaMenuForPlaylistNumbers()
    {
        var folder = Directory.CreateTempSubdirectory("ripparr-jar").FullName;
        try
        {
            var jar = Path.Combine(folder, "00000.jar");
            using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
            {
                Write(zip, "com/studio/Menu.class", "Êþº¾...bluray://PLAYLIST:00118...");
                Write(zip, "config/episodes.xml", "<ep playlist=\"152\"/><ep file=\"00117.mpls\"/><ep>bd://PLAYLIST:00118</ep><x>00004</x>");
            }

            var (order, file) = JarScan.Order([jar], Episodes);
            CollectionAssert.AreEqual(new[] { "00152", "00117", "00118" }, order);
            Assert.AreEqual("00000.jar/config/episodes.xml", file);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.Latin1.GetBytes(text));
    }

    [TestMethod]
    public void AFolderDiscGivesItsMenuOrderToTheEpisodes()
    {
        var folder = Directory.CreateTempSubdirectory("ripparr-menu").FullName;
        try
        {
            var stream = Directory.CreateDirectory(Path.Combine(folder, "BDMV", "STREAM")).FullName;
            File.WriteAllBytes(Path.Combine(stream, "00001.m2ts"), new byte[192 * 10]); // not a menu
            File.WriteAllBytes(Path.Combine(stream, "00003.m2ts"), DoctorWhoMenu());

            var (order, note) = MenuReader.FromFolder(folder, ["00001", "00003"], Episodes);
            CollectionAssert.AreEqual(new[] { "00152", "00117", "00118" }, order);
            StringAssert.Contains(note, "clip 00003");

            var disc = new DiscAnalysis
            {
                Source = new DiscSource("/x.iso", SourceKind.Iso, "/", 0, DateTime.UtcNow),
                Playlists = Episodes.Select((n, i) => new PlaylistInfo { Name = n, Mpls = Mpls.Parse(MplsBuilder.Feature(($"{10 + i:00000}", 2700)).Build()) }).ToList(),
                MenuOrder = order,
                MenuNote = note
            };
            var (episodes, how) = EpisodeFinder.InDiscOrder(disc, disc.Playlists);
            CollectionAssert.AreEqual(new[] { "00152", "00117", "00118" }, episodes.Select(e => e.Name).ToArray());
            StringAssert.Contains(how, "menu");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
