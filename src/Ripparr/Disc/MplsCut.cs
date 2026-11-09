using System.Buffers.Binary;

namespace Ripparr.Disc;

/// <summary>
/// Cuts one play item out of a disc's playlist as a playlist of its own: a deleted scene out of a
/// reel that plays them all one after the other. The play item is copied byte for byte (clip, in
/// and out times, every stream as the disc lists it), its chapter marks come along, and the rest
/// (other items, sub paths, extension data) is left out.
/// </summary>
public static class MplsCut
{
    public static byte[] Piece(byte[] raw, int index)
    {
        var data = raw.AsSpan();
        if (data.Length < 40)
        {
            throw new InvalidDataException("Not a Blu-ray playlist.");
        }

        var playlistStart = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var marksStart = (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        var pos = playlistStart + 4 + 2; // length, reserved
        var itemCount = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
        pos += 4; // item count, sub path count
        if (index < 0 || index >= itemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"The playlist has {itemCount} play items.");
        }

        byte[] item = [];
        for (var i = 0; i <= index; i++)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            if (i == index)
            {
                item = data.Slice(pos + 2, length).ToArray();
            }

            pos += 2 + length;
        }

        var inTime = BinaryPrimitives.ReadUInt32BigEndian(item.AsSpan(12));

        // The item's entry marks (chapters), now on item 0; one at its start when it has none there.
        var marks = new List<(uint Time, ushort Pid, uint Duration)>();
        if (marksStart > 0 && marksStart + 6 <= data.Length)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(data[(marksStart + 4)..]);
            for (var m = 0; m < count && marksStart + 6 + m * 14 + 14 <= data.Length; m++)
            {
                var at = data[(marksStart + 6 + m * 14)..];
                if (at[1] == 1 && BinaryPrimitives.ReadUInt16BigEndian(at[2..]) == index)
                {
                    marks.Add((BinaryPrimitives.ReadUInt32BigEndian(at[4..]), BinaryPrimitives.ReadUInt16BigEndian(at[8..]),
                        BinaryPrimitives.ReadUInt32BigEndian(at[10..])));
                }
            }
        }

        if (!marks.Any(m => m.Time <= inTime + Mpls.ClockRate))
        {
            marks.Insert(0, (inTime, 0xFFFF, 0));
        }

        var section = new List<byte>();
        Put16(section, 0); // reserved
        Put16(section, 1); // one play item
        Put16(section, 0); // no sub paths
        Put16(section, item.Length);
        section.AddRange(item);

        var markBytes = new List<byte>();
        Put16(markBytes, marks.Count);
        foreach (var (time, pid, duration) in marks.OrderBy(m => m.Time))
        {
            markBytes.Add(0);
            markBytes.Add(1); // entry mark
            Put16(markBytes, 0);
            Put32(markBytes, time);
            Put16(markBytes, pid);
            Put32(markBytes, duration);
        }

        // Header and AppInfoPlayList as on the disc, with the new marks offset and no extension data.
        var head = data[..playlistStart].ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), (uint)(playlistStart + 4 + section.Count));
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(16), 0);
        var output = new List<byte>(head);

        Put32(output, section.Count);
        output.AddRange(section);
        Put32(output, markBytes.Count);
        output.AddRange(markBytes);
        return output.ToArray();
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
