using System.Buffers.Binary;
using System.Text;

namespace Ripparr.Disc;

/// <summary>
/// A clip's info file (BDMV/CLIPINF/xxxxx.clpi): when the clip starts and ends, and its streams
/// with their languages. Read as libbluray does (clpi_parse.c). Some discs (The Fabelmans UHD)
/// have no playlist for the film at all: their BD-J menu builds it at play time, and the film is
/// one big clip. Ripparr then makes the playlist itself from this file.
/// </summary>
public sealed class Clpi
{
    public sealed record ClipStream(int Pid, byte Coding, byte Format, byte Rate, string? Language,
        byte DynamicRange, byte ColorSpace, bool Hdr10Plus);

    public required string Version { get; init; }

    /// <summary>45 kHz time stamps of the first and last picture (the first STC sequence).</summary>
    public uint Start { get; init; }

    public uint End { get; init; }

    public required IReadOnlyList<ClipStream> Streams { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds(((long)End - Start) / (double)Mpls.ClockRate);

    public static Clpi Read(string path) => Parse(File.ReadAllBytes(path));

    public static Clpi Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || Encoding.ASCII.GetString(data[..4]) != "HDMV")
        {
            throw new InvalidDataException("Not a clip info file (no HDMV header).");
        }

        var version = Encoding.ASCII.GetString(data.Slice(4, 4));
        var sequenceStart = (int)U32(data, 8);
        var programStart = (int)U32(data, 12);

        // SequenceInfo: length(32) reserved(8) ATC count(8), each ATC: SPN(32) STC count(8) offset(8),
        // each STC: PCR PID(16) SPN(32) start(32) end(32). The film runs over the longest STC sequence.
        uint start = 0, end = 0;
        Check(data, sequenceStart, 6);
        var atcCount = data[sequenceStart + 5];
        var pos = sequenceStart + 6;
        for (var a = 0; a < atcCount; a++)
        {
            Check(data, pos, 6);
            var stcCount = data[pos + 4];
            pos += 6;
            for (var s = 0; s < stcCount; s++)
            {
                Check(data, pos, 14);
                var stcStart = U32(data, pos + 6);
                var stcEnd = U32(data, pos + 10);
                if ((long)stcEnd - stcStart > (long)end - start)
                {
                    (start, end) = (stcStart, stcEnd);
                }

                pos += 14;
            }
        }

        // ProgramInfo: length(32) reserved(8) program count(8), each program: SPN(32) PMT PID(16)
        // stream count(8) group count(8), each stream: PID(16) then a length-prefixed coding info.
        var streams = new List<ClipStream>();
        Check(data, programStart, 6);
        var programCount = data[programStart + 5];
        pos = programStart + 6;
        for (var p = 0; p < programCount; p++)
        {
            Check(data, pos, 8);
            var streamCount = data[pos + 6];
            pos += 8;
            for (var s = 0; s < streamCount; s++)
            {
                Check(data, pos, 3);
                var pid = U16(data, pos);
                var length = data[pos + 2];
                Check(data, pos + 3, length);
                var info = data.Slice(pos + 3, length);
                pos += 3 + length;
                if (info.Length > 0 && Stream(pid, info) is { } stream)
                {
                    streams.Add(stream);
                }
            }
        }

        return new Clpi { Version = version, Start = start, End = end, Streams = streams };
    }

    private static ClipStream? Stream(int pid, ReadOnlySpan<byte> info)
    {
        var coding = info[0];
        byte format = 0, rate = 0, range = 0, space = 0;
        var hdr10Plus = false;
        string? language = null;
        switch (StreamCoding.Kind(coding))
        {
            case StreamKind.Video:
                if (info.Length > 1)
                {
                    format = (byte)(info[1] >> 4);
                    rate = (byte)(info[1] & 0x0F);
                }

                // HEVC: aspect(4) reserved(2) oc(1) cr(1), then dynamic range(4) colour space(4), HDR10+(1).
                if (coding == 0x24 && info.Length > 3)
                {
                    range = (byte)(info[3] >> 4);
                    space = (byte)(info[3] & 0x0F);
                    hdr10Plus = info.Length > 4 && (info[4] & 0x80) != 0;
                }

                break;
            case StreamKind.Audio:
                if (info.Length > 1)
                {
                    format = (byte)(info[1] >> 4);
                    rate = (byte)(info[1] & 0x0F);
                }

                language = info.Length >= 5 ? Language(info.Slice(2, 3)) : null;
                break;
            case StreamKind.Subtitle:
                language = coding == 0x92
                    ? info.Length >= 5 ? Language(info.Slice(2, 3)) : null
                    : info.Length >= 4 ? Language(info.Slice(1, 3)) : null;
                break;
            default:
                return null; // menus (IG) and anything else are not ripped
        }

        return new ClipStream(pid, coding, format, rate, language, range, space, hdr10Plus);
    }

    /// <summary>
    /// A playlist that plays the whole clip with all its streams, as the disc's own would. PIDs
    /// follow the Blu-ray layout: 0x1011 the film, 0x1015 the Dolby Vision layer, 0x1Bxx picture
    /// in picture (left out), 0x1Axx secondary audio (left out).
    /// </summary>
    public MplsWriter ToPlaylist(string clip)
    {
        var writer = new MplsWriter { Version = Version is "0100" or "0200" or "0300" ? Version : "0300" };
        writer.Items.Add(new MplsWriter.Item(clip, Start, End));
        foreach (var s in Streams)
        {
            switch (StreamCoding.Kind(s.Coding))
            {
                case StreamKind.Video when s.Pid is >= 0x1B00 and <= 0x1B1F:
                    break;
                case StreamKind.Video when s.Pid == 0x1015 || (writer.Video.Count > 0 && s.Coding == 0x24):
                    writer.DolbyVision.Add(new MplsWriter.Stream(s.Coding, s.Pid, Format: s.Format, Rate: s.Rate));
                    break;
                case StreamKind.Video:
                    writer.Video.Add(new MplsWriter.Stream(s.Coding, s.Pid, Format: s.Format, Rate: s.Rate,
                        Hdr: (byte)((s.DynamicRange << 4) | s.ColorSpace), HdrFlags: (byte)(s.Hdr10Plus ? 0x40 : 0)));
                    break;
                case StreamKind.Audio when s.Pid is >= 0x1A00 and <= 0x1A1F:
                    break;
                case StreamKind.Audio:
                    writer.Audio.Add(new MplsWriter.Stream(s.Coding, s.Pid, s.Language, s.Format, s.Rate));
                    break;
                case StreamKind.Subtitle:
                    writer.Subtitles.Add(new MplsWriter.Stream(s.Coding, s.Pid, s.Language));
                    break;
            }
        }

        return writer;
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
            throw new InvalidDataException("Clip info file is cut short.");
        }
    }
}
