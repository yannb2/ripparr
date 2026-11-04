using System.Buffers.Binary;

namespace Ripparr.Disc;

/// <summary>
/// Pulls PGS subtitle tracks out of Blu-ray transport stream packets and writes each one as a
/// .sup file ("PG", PTS, DTS, then the segment), the format the subtitle preview reads. Times
/// are put on the playlist's clock: each clip's in-time counts from where its play item starts.
/// </summary>
public sealed class PgsExtractor : IDisposable
{
    private const int PacketSize = 192;

    private readonly Dictionary<int, Track> _tracks = new();

    /// <summary>Where the current play item starts in the playlist and which part of its clip it plays (90 kHz).</summary>
    private long _offset90;

    private long _in90;
    private long _out90 = long.MaxValue;

    public PgsExtractor(IReadOnlyDictionary<int, Stream> outputs)
    {
        foreach (var (pid, output) in outputs)
        {
            _tracks[pid] = new Track(output);
        }
    }

    /// <summary>Starts a play item: <paramref name="offset"/> into the playlist, its clip from in to out (45 kHz ticks).</summary>
    public void StartItem(long offset45k, uint inTime, uint outTime)
    {
        foreach (var track in _tracks.Values)
        {
            Flush(track);
        }

        _offset90 = offset45k * 2;
        _in90 = (long)inTime * 2;
        _out90 = (long)outTime * 2;
    }

    /// <summary>
    /// The next data does not follow on from the last (a quick read jumps to another part of the
    /// film): PES packets cut short are dropped, not written.
    /// </summary>
    public void Break()
    {
        foreach (var track in _tracks.Values)
        {
            track.Collecting = false;
            track.Pes.SetLength(0);
        }
    }

    /// <summary>Takes whole 192-byte packets; anything left over at the end is ignored.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        for (var at = 0; at + PacketSize <= data.Length; at += PacketSize)
        {
            var ts = data.Slice(at + 4, 188);
            if (ts[0] != 0x47)
            {
                continue;
            }

            var pid = ((ts[1] & 0x1F) << 8) | ts[2];
            if (!_tracks.TryGetValue(pid, out var track))
            {
                continue;
            }

            var start = (ts[1] & 0x40) != 0;
            var adaptation = (ts[3] >> 4) & 0x3;
            var payload = 4;
            if ((adaptation & 0x2) != 0)
            {
                payload += 1 + ts[4];
            }

            if ((adaptation & 0x1) == 0 || payload >= 188)
            {
                continue;
            }

            if (start)
            {
                Flush(track);
                track.Collecting = true;
            }

            if (track.Collecting)
            {
                track.Pes.Write(ts[payload..]);
            }
        }
    }

    public void Finish()
    {
        foreach (var track in _tracks.Values)
        {
            Flush(track);
            track.Output.Flush();
        }
    }

    /// <summary>One PES packet: its PTS, then the segments inside, each written with a .sup header.</summary>
    private void Flush(Track track)
    {
        if (!track.Collecting)
        {
            return;
        }

        track.Collecting = false;
        var pes = track.Pes.GetBuffer().AsSpan(0, (int)track.Pes.Length);
        track.Pes.SetLength(0);
        if (pes.Length < 9 || pes[0] != 0 || pes[1] != 0 || pes[2] != 1)
        {
            return;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(pes[4..]);
        if (length > 0 && 6 + length <= pes.Length)
        {
            pes = pes[..(6 + length)];
        }

        var flags = pes[7] >> 6;
        var headerEnd = 9 + pes[8];
        if (headerEnd > pes.Length)
        {
            return;
        }

        long pts = (flags & 0x2) != 0 && pes.Length >= 14 ? Timestamp(pes[9..]) : track.LastPts;
        long dts = flags == 3 && pes.Length >= 19 ? Timestamp(pes[14..]) : 0;
        track.LastPts = pts;
        if (pts < _in90 || pts >= _out90)
        {
            return; // outside the part of the clip this play item plays
        }

        var playlistPts = (uint)(pts - _in90 + _offset90);
        var playlistDts = dts == 0 ? 0u : (uint)Math.Max(0, dts - _in90 + _offset90);
        var segments = pes[headerEnd..];
        Span<byte> header = stackalloc byte[13];
        var position = 0;
        while (position + 3 <= segments.Length)
        {
            var size = BinaryPrimitives.ReadUInt16BigEndian(segments[(position + 1)..]);
            if (position + 3 + size > segments.Length)
            {
                break;
            }

            header[0] = (byte)'P';
            header[1] = (byte)'G';
            BinaryPrimitives.WriteUInt32BigEndian(header[2..], playlistPts);
            BinaryPrimitives.WriteUInt32BigEndian(header[6..], playlistDts);
            header[10] = segments[position];
            BinaryPrimitives.WriteUInt16BigEndian(header[11..], size);
            track.Output.Write(header);
            track.Output.Write(segments.Slice(position + 3, size));
            position += 3 + size;
        }
    }

    /// <summary>A 33-bit PES time stamp (90 kHz) from its 5 bytes.</summary>
    public static long Timestamp(ReadOnlySpan<byte> b) =>
        ((long)(b[0] >> 1) & 0x7) << 30 | (long)b[1] << 22 | (long)(b[2] >> 1) << 15 | (long)b[3] << 7 | (long)(b[4] >> 1);

    public void Dispose()
    {
        foreach (var track in _tracks.Values)
        {
            track.Pes.Dispose();
        }
    }

    private sealed class Track(Stream output)
    {
        public Stream Output { get; } = output;
        public MemoryStream Pes { get; } = new();
        public bool Collecting { get; set; }
        public long LastPts { get; set; }
    }
}
