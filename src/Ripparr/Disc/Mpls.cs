using System.Buffers.Binary;
using System.Text;

namespace Ripparr.Disc;

/// <summary>One stream listed in a playlist's STN table, with what the disc says about it.</summary>
public sealed record PlaylistStream
{
    public required StreamKind Kind { get; init; }

    /// <summary>The stream coding type (0x24 = HEVC, 0x83 = TrueHD...).</summary>
    public required byte Coding { get; init; }

    /// <summary>The transport stream PID (0x1011, 0x1100...).</summary>
    public int Pid { get; init; }

    /// <summary>ISO 639-2 code ("eng"), for audio and subtitles.</summary>
    public string? Language { get; init; }

    /// <summary>Video format or audio presentation type, raw.</summary>
    public byte Format { get; init; }

    /// <summary>Video frame rate or audio sampling frequency, raw.</summary>
    public byte Rate { get; init; }

    /// <summary>UHD (HEVC) only: 0 SDR, 1 HDR10, 2 Dolby Vision, as libbluray reads the STN table.</summary>
    public byte DynamicRange { get; init; }

    /// <summary>UHD (HEVC) only: 1 BT.709, 2 BT.2020.</summary>
    public byte ColorSpace { get; init; }

    /// <summary>UHD (HEVC) only: the disc says the stream carries HDR10+ metadata.</summary>
    public bool Hdr10Plus { get; init; }

    /// <summary>True for the Dolby Vision enhancement layer of a UHD disc.</summary>
    public bool DolbyVisionEnhancementLayer { get; init; }

    /// <summary>
    /// The sub path that carries the stream when it is not in the play item's own clip (stream
    /// entry type 2, 3 or 4): some Dolby Vision discs keep the enhancement layer in clips of its own.
    /// </summary>
    public int? SubPath { get; init; }

    public string CodecName => StreamCoding.Name(Coding);
}

/// <summary>A sub path of a playlist: its type and the clips of its sub play items, in order.</summary>
public sealed record SubPath(int Type, IReadOnlyList<string> Clips);

public sealed record PlayItem(string Clip, uint InTime, uint OutTime, int ConnectionCondition)
{
    /// <summary>Ticks of the 45 kHz clock.</summary>
    public long Duration45k => (long)OutTime - InTime;
}

/// <summary>A chapter mark: when, from the start of the playlist.</summary>
public sealed record ChapterMark(TimeSpan Start);

/// <summary>A Blu-ray playlist (BDMV/PLAYLIST/00800.mpls): which clips play in which order, the streams and the chapters.</summary>
public sealed class Mpls
{
    public const int ClockRate = 45000;

    public required string Version { get; init; }
    public required IReadOnlyList<PlayItem> Items { get; init; }

    /// <summary>The streams of the first play item: every play item of a feature lists the same ones.</summary>
    public required IReadOnlyList<PlaylistStream> Streams { get; init; }

    public required IReadOnlyList<ChapterMark> Chapters { get; init; }

    public IReadOnlyList<SubPath> SubPaths { get; init; } = [];

    /// <summary>The same playlist with the Dolby Vision layer under the PID its clip really uses.</summary>
    public Mpls WithLayerPid(int pid) => new()
    {
        Version = Version,
        Items = Items,
        Streams = Streams.Select(s => s.DolbyVisionEnhancementLayer ? s with { Pid = pid } : s).ToList(),
        Chapters = Chapters,
        SubPaths = SubPaths
    };

    /// <summary>
    /// The clips that hold the Dolby Vision enhancement layer when it sits in a sub path (its own
    /// .m2ts files), else null: then it is in the play items' clips, next to the base layer.
    /// </summary>
    public IReadOnlyList<string>? EnhancementLayerClips =>
        Streams.FirstOrDefault(s => s.DolbyVisionEnhancementLayer) is { SubPath: { } id } && id < SubPaths.Count && SubPaths[id].Clips.Count > 0
            ? SubPaths[id].Clips
            : null;

    public TimeSpan Duration => TimeSpan.FromSeconds(Items.Sum(i => i.Duration45k) / (double)ClockRate);

    public bool HasDolbyVisionEnhancementLayer => Streams.Any(s => s.DolbyVisionEnhancementLayer);

    /// <summary>The clips in play order, without repeats ("00055", "00056"...).</summary>
    public IReadOnlyList<string> Clips => Items.Select(i => i.Clip).ToList();

    public static Mpls Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Reads the playlist as libbluray does (mpls_parse.c). Throws <see cref="InvalidDataException"/> on anything else.</summary>
    public static Mpls Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || Encoding.ASCII.GetString(data[..4]) != "MPLS")
        {
            throw new InvalidDataException("Not a Blu-ray playlist (no MPLS header).");
        }

        var version = Encoding.ASCII.GetString(data.Slice(4, 4));
        var playlistStart = (int)U32(data, 8);
        var marksStart = (int)U32(data, 12);
        Check(data, playlistStart, 10);

        var pos = playlistStart + 4 + 2; // length, reserved
        var itemCount = U16(data, pos);
        var subPathCount = U16(data, pos + 2);
        pos += 4; // item count, subpath count

        var items = new List<PlayItem>(itemCount);
        IReadOnlyList<PlaylistStream>? streams = null;
        for (var i = 0; i < itemCount; i++)
        {
            Check(data, pos, 2);
            var length = U16(data, pos);
            var start = pos + 2;
            Check(data, start, length);
            var (item, itemStreams) = ParsePlayItem(data.Slice(start, length));
            items.Add(item);
            streams ??= itemStreams;
            pos = start + length;
        }

        var subPaths = new List<SubPath>();
        for (var i = 0; i < subPathCount && pos + 10 <= data.Length; i++)
        {
            var length = (int)U32(data, pos);
            var start = pos + 4;
            if (start + length > data.Length || length < 6)
            {
                break;
            }

            subPaths.Add(ParseSubPath(data.Slice(start, length)));
            pos = start + length;
        }

        var chapters = new List<ChapterMark>();
        if (marksStart > 0 && marksStart + 6 <= data.Length)
        {
            var count = U16(data, marksStart + 4);
            var offsets = new List<long>();
            long total = 0;
            foreach (var item in items)
            {
                offsets.Add(total);
                total += item.Duration45k;
            }

            for (var m = 0; m < count; m++)
            {
                var at = marksStart + 6 + m * 14;
                if (at + 14 > data.Length)
                {
                    break;
                }

                var type = data[at + 1];
                var itemIndex = U16(data, at + 2);
                var stamp = U32(data, at + 4);
                if (type != 1 || itemIndex >= items.Count)
                {
                    continue; // only entry marks are chapters
                }

                var ticks = offsets[itemIndex] + (long)stamp - items[itemIndex].InTime;
                if (ticks < 0 || ticks > total)
                {
                    continue;
                }

                chapters.Add(new ChapterMark(TimeSpan.FromSeconds(ticks / (double)ClockRate)));
            }
        }

        return new Mpls
        {
            Version = version,
            Items = items,
            Streams = streams ?? [],
            SubPaths = subPaths,
            Chapters = chapters
                .DistinctBy(c => Math.Round(c.Start.TotalMilliseconds))
                .OrderBy(c => c.Start)
                .ToList()
        };
    }

    private static (PlayItem Item, IReadOnlyList<PlaylistStream> Streams) ParsePlayItem(ReadOnlySpan<byte> item)
    {
        if (item.Length < 34)
        {
            throw new InvalidDataException("Play item too short.");
        }

        var clip = Encoding.ASCII.GetString(item[..5]);
        var flags = U16(item, 9);
        var multiAngle = (flags & 0x10) != 0;
        var connection = flags & 0x0F;
        var inTime = U32(item, 12);
        var outTime = U32(item, 16);

        var pos = 20 + 8 + 1 + 1 + 2; // UO mask, random access, still mode, still time
        if (multiAngle)
        {
            Check(item, pos, 2);
            var angles = item[pos];
            pos += 2 + Math.Max(0, angles - 1) * 10;
        }

        var streams = new List<PlaylistStream>();
        if (pos + 16 <= item.Length)
        {
            var stnLength = U16(item, pos);
            var stn = item.Slice(pos + 2, Math.Min(stnLength, item.Length - pos - 2));
            streams = ParseStn(stn);
        }

        return (new PlayItem(clip, inTime, outTime, connection), streams);
    }

    /// <summary>reserved(8) type(8) flags(16) reserved(8) count(8), then each sub play item: length(16), clip name(5)...</summary>
    private static SubPath ParseSubPath(ReadOnlySpan<byte> path)
    {
        var type = path[1];
        var count = path[5];
        var clips = new List<string>();
        var pos = 6;
        for (var i = 0; i < count && pos + 7 <= path.Length; i++)
        {
            var length = U16(path, pos);
            clips.Add(Encoding.ASCII.GetString(path.Slice(pos + 2, 5)));
            pos += 2 + length;
        }

        return new SubPath(type, clips);
    }

    private static List<PlaylistStream> ParseStn(ReadOnlySpan<byte> stn)
    {
        var streams = new List<PlaylistStream>();
        if (stn.Length < 14)
        {
            return streams;
        }

        int video = stn[2], audio = stn[3], pg = stn[4], ig = stn[5];
        int secondaryAudio = stn[6], secondaryVideo = stn[7], pipPg = stn[8], dv = stn[9];
        var pos = 14;

        int? subPath = null;
        bool Next(ReadOnlySpan<byte> s, ref int p, out int pid, out ReadOnlySpan<byte> attributes)
        {
            pid = 0;
            subPath = null;
            attributes = default;
            if (p >= s.Length)
            {
                return false;
            }

            var entryLength = s[p];
            var entry = s.Slice(p + 1, Math.Min(entryLength, s.Length - p - 1));
            pid = entry.Length switch
            {
                >= 3 when entry[0] == 1 => U16(entry, 1),
                >= 5 when entry[0] is 2 or 4 => U16(entry, 3),
                >= 4 when entry[0] == 3 => U16(entry, 2),
                _ => 0
            };
            if (entry.Length >= 2 && entry[0] is 2 or 3 or 4)
            {
                subPath = entry[1];
            }

            p += 1 + entryLength;
            if (p >= s.Length)
            {
                return false;
            }

            var attributesLength = s[p];
            attributes = s.Slice(p + 1, Math.Min(attributesLength, s.Length - p - 1));
            p += 1 + attributesLength;
            return attributes.Length > 0;
        }

        for (var i = 0; i < video && Next(stn, ref pos, out var pid, out var a); i++)
        {
            streams.Add(new PlaylistStream
            {
                Kind = StreamKind.Video, Coding = a[0], Pid = pid,
                Format = a.Length > 1 ? (byte)(a[1] >> 4) : (byte)0,
                Rate = a.Length > 1 ? (byte)(a[1] & 0x0F) : (byte)0,
                // HEVC: dynamic range type (4 bits), colour space (4 bits), CR flag, HDR10+ flag.
                DynamicRange = a[0] == 0x24 && a.Length > 2 ? (byte)(a[2] >> 4) : (byte)0,
                ColorSpace = a[0] == 0x24 && a.Length > 2 ? (byte)(a[2] & 0x0F) : (byte)0,
                Hdr10Plus = a[0] == 0x24 && a.Length > 3 && (a[3] & 0x40) != 0
            });
        }

        for (var i = 0; i < audio && Next(stn, ref pos, out var pid, out var a); i++)
        {
            streams.Add(new PlaylistStream
            {
                Kind = StreamKind.Audio, Coding = a[0], Pid = pid,
                Format = a.Length > 1 ? (byte)(a[1] >> 4) : (byte)0,
                Rate = a.Length > 1 ? (byte)(a[1] & 0x0F) : (byte)0,
                Language = a.Length >= 5 ? Language(a.Slice(2, 3)) : null
            });
        }

        for (var i = 0; i < pg + pipPg && Next(stn, ref pos, out var pid, out var a); i++)
        {
            var language = a[0] == 0x92 ? (a.Length >= 5 ? a.Slice(2, 3) : default) : (a.Length >= 4 ? a.Slice(1, 3) : default);
            streams.Add(new PlaylistStream
            {
                Kind = StreamKind.Subtitle, Coding = a[0], Pid = pid,
                Language = language.Length == 3 ? Language(language) : null
            });
        }

        // Interactive graphics (menus), secondary audio and video (picture in picture): skipped,
        // but read past so the Dolby Vision layer after them is found.
        for (var i = 0; i < ig && Next(stn, ref pos, out _, out _); i++)
        {
        }

        for (var i = 0; i < secondaryAudio && Next(stn, ref pos, out _, out _); i++)
        {
            if (pos + 2 > stn.Length) break;
            var refs = stn[pos];
            pos += 2 + refs + (refs % 2);
        }

        for (var i = 0; i < secondaryVideo && Next(stn, ref pos, out _, out _); i++)
        {
            if (pos + 2 > stn.Length) break;
            var audioRefs = stn[pos];
            pos += 2 + audioRefs + (audioRefs % 2);
            if (pos + 2 > stn.Length) break;
            var pgRefs = stn[pos];
            pos += 2 + pgRefs + (pgRefs % 2);
        }

        for (var i = 0; i < dv && Next(stn, ref pos, out var pid, out var a); i++)
        {
            streams.Add(new PlaylistStream
            {
                Kind = StreamKind.Video, Coding = a[0], Pid = pid, DolbyVisionEnhancementLayer = true, SubPath = subPath,
                Format = a.Length > 1 ? (byte)(a[1] >> 4) : (byte)0,
                Rate = a.Length > 1 ? (byte)(a[1] & 0x0F) : (byte)0
            });
        }

        return streams;
    }

    private static string? Language(ReadOnlySpan<byte> code)
    {
        var text = Encoding.ASCII.GetString(code).Trim('\0', ' ').ToLowerInvariant();
        return text.Length == 3 && text.All(char.IsAsciiLetterLower) ? text : null;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static void Check(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException("Playlist is cut short.");
        }
    }
}
