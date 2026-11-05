using System.Buffers.Binary;
using System.Text;

namespace Ripparr.Disc;

/// <summary>
/// Writes small but valid MPLS files, following the layout libbluray reads: the playlist Ripparr
/// makes for a film clip no playlist on the disc uses (and the tests' playlists).
/// </summary>
public class MplsWriter
{
    /// <param name="Hdr">UHD video: dynamic range type and colour space, as the third attribute byte (0x22 = DV, BT.2020).</param>
    /// <param name="HdrFlags">UHD video: the fourth attribute byte (0x40 = HDR10+).</param>
    public sealed record Stream(byte Coding, int Pid, string? Language = null, byte Format = 0, byte Rate = 0, byte Hdr = 0, byte HdrFlags = 0);

    public sealed record Item(string Clip, uint In, uint Out);

    public List<Item> Items { get; } = [];
    public List<Stream> Video { get; } = [];
    public List<Stream> Audio { get; } = [];
    public List<Stream> Subtitles { get; } = [];
    public List<Stream> DolbyVision { get; } = [];

    /// <summary>When set, the Dolby Vision layer sits in a sub path (type 10) made of these clips.</summary>
    public List<string> LayerClips { get; } = [];

    /// <summary>(play item index, 45 kHz time stamp) of each chapter mark.</summary>
    public List<(int Item, uint Time)> Marks { get; } = [];

    public string Version { get; set; } = "0300";

    /// <summary>Adds a play item per clip, each running the given seconds.</summary>
    public void AddClips(params (string Clip, double Seconds)[] clips)
    {
        foreach (var (clip, seconds) in clips)
        {
            Items.Add(new Item(clip, 27_000_000, 27_000_000 + (uint)(seconds * 45000)));
        }
    }

    public byte[] Build()
    {
        var playlist = new List<byte>();
        Put16(playlist, 0); // reserved
        Put16(playlist, Items.Count);
        Put16(playlist, LayerClips.Count > 0 ? 1 : 0); // sub paths
        foreach (var item in Items)
        {
            var body = PlayItem(item);
            Put16(playlist, body.Count);
            playlist.AddRange(body);
        }

        if (LayerClips.Count > 0)
        {
            var path = new List<byte> { 0, 10, 0, 0, 0, (byte)LayerClips.Count };
            foreach (var clip in LayerClips)
            {
                var sub = new List<byte>();
                sub.AddRange(Encoding.ASCII.GetBytes(clip.PadLeft(5, '0')));
                sub.AddRange(Encoding.ASCII.GetBytes("M2TS"));
                sub.AddRange(new byte[12]); // connection, STC id, in and out times
                Put16(path, sub.Count);
                path.AddRange(sub);
            }

            Put32(playlist, path.Count);
            playlist.AddRange(path);
        }

        var marks = new List<byte>();
        Put16(marks, Marks.Count);
        foreach (var (item, time) in Marks)
        {
            marks.Add(0);
            marks.Add(1); // entry mark
            Put16(marks, item);
            Put32(marks, time);
            Put16(marks, 0xFFFF);
            Put32(marks, 0);
        }

        var header = new List<byte>();
        header.AddRange(Encoding.ASCII.GetBytes("MPLS" + Version));
        var playlistStart = 40 + 4 + 2 + 8 + 2; // header + a minimal AppInfoPlayList
        var marksStart = playlistStart + 4 + playlist.Count;
        Put32(header, playlistStart);
        Put32(header, marksStart);
        Put32(header, 0);
        header.AddRange(new byte[20]);
        // AppInfoPlayList: length, reserved, playback type, reserved, UO mask, flags.
        Put32(header, 2 + 8 + 2);
        header.AddRange(new byte[2 + 8 + 2]);

        var all = new List<byte>(header);
        Put32(all, playlist.Count);
        all.AddRange(playlist);
        Put32(all, marks.Count);
        all.AddRange(marks);
        return all.ToArray();
    }

    private List<byte> PlayItem(Item item)
    {
        var body = new List<byte>();
        body.AddRange(Encoding.ASCII.GetBytes(item.Clip.PadLeft(5, '0')));
        body.AddRange(Encoding.ASCII.GetBytes("M2TS"));
        Put16(body, 1); // not multi angle, connection condition 1
        body.Add(0); // STC id
        Put32(body, item.In);
        Put32(body, item.Out);
        body.AddRange(new byte[8]); // UO mask
        body.Add(0); // random access
        body.Add(0); // still mode
        Put16(body, 0); // still time

        var stn = new List<byte>();
        Put16(stn, 0);
        stn.Add((byte)Video.Count);
        stn.Add((byte)Audio.Count);
        stn.Add((byte)Subtitles.Count);
        stn.Add(0); // IG
        stn.Add(0); // secondary audio
        stn.Add(0); // secondary video
        stn.Add(0); // PIP PG
        stn.Add((byte)DolbyVision.Count);
        stn.AddRange(new byte[4]);
        foreach (var s in Video)
        {
            Entry(stn, s.Pid);
            stn.AddRange(new byte[] { 5, s.Coding, (byte)((s.Format << 4) | s.Rate), s.Hdr, s.HdrFlags, 0 });
        }

        foreach (var s in Audio)
        {
            Entry(stn, s.Pid);
            var language = Encoding.ASCII.GetBytes(s.Language ?? "und");
            stn.AddRange(new byte[] { 5, s.Coding, (byte)((s.Format << 4) | s.Rate), language[0], language[1], language[2] });
        }

        foreach (var s in Subtitles)
        {
            Entry(stn, s.Pid);
            var language = Encoding.ASCII.GetBytes(s.Language ?? "und");
            stn.AddRange(new byte[] { 5, s.Coding, language[0], language[1], language[2], 0 });
        }

        foreach (var s in DolbyVision)
        {
            if (LayerClips.Count > 0)
            {
                // Stream in a sub path clip: type 2, sub path id, sub clip id, PID.
                stn.AddRange(new byte[] { 9, 2, 0, 0 });
                Put16(stn, s.Pid);
                stn.AddRange(new byte[4]);
            }
            else
            {
                Entry(stn, s.Pid);
            }

            stn.AddRange(new byte[] { 5, s.Coding, (byte)((s.Format << 4) | s.Rate), 0, 0, 0 });
        }

        Put16(body, stn.Count);
        body.AddRange(stn);
        return body;
    }

    private static void Entry(List<byte> stn, int pid)
    {
        stn.AddRange(new byte[] { 9, 1 });
        Put16(stn, pid);
        stn.AddRange(new byte[6]);
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
