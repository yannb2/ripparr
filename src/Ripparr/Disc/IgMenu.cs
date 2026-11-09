namespace Ripparr.Disc;

/// <summary>One HDMV navigation command (12 bytes), as a menu button or a movie object runs it.</summary>
public sealed record HdmvCommand(int Group, int SubGroup, int BranchOption, int SetOption, bool ImmediateDst, bool ImmediateSrc, uint Dst, uint Src)
{
    public const int GroupBranch = 0, GroupSet = 2;
    public const int SubGroupPlay = 2, SubGroupSet = 0;
    public const int PlayPl = 0, PlayPlAtItem = 1, PlayPlAtMark = 2, Move = 1;

    /// <summary>A play command (PlayPL, PlayPLatPI, PlayPLatMK).</summary>
    public bool Plays => Group == GroupBranch && SubGroup == SubGroupPlay && BranchOption is PlayPl or PlayPlAtItem or PlayPlAtMark;

    /// <summary>"Move GPRn, value": the menu's way of telling a movie object what to play.</summary>
    public bool MovesValueToGpr => Group == GroupSet && SubGroup == SubGroupSet && SetOption == Move && !ImmediateDst && (Dst & 0x80000000) == 0 && ImmediateSrc;

    public static HdmvCommand Parse(ReadOnlySpan<byte> b) => new(
        (b[0] >> 3) & 0x03, b[0] & 0x07, b[1] & 0x0F, b[3] & 0x1F, (b[1] & 0x80) != 0, (b[1] & 0x40) != 0,
        (uint)(b[4] << 24 | b[5] << 16 | b[6] << 8 | b[7]), (uint)(b[8] << 24 | b[9] << 16 | b[10] << 8 | b[11]));
}

/// <summary>A button of the disc menu: where it sits on its page and what pressing it runs.</summary>
public sealed record MenuButton(int Page, int Id, int NumericSelect, int X, int Y, IReadOnlyList<HdmvCommand> Commands)
{
    /// <summary>
    /// The playlist numbers pressing it plays: PlayPL with a number, or a number moved into a
    /// register (the movie object then plays the playlist that register holds).
    /// </summary>
    public IEnumerable<uint> PlaylistNumbers => Commands
        .Where(c => c.Plays && c.ImmediateDst || c.MovesValueToGpr)
        .Select(c => c.Plays ? c.Dst : c.Src);
}

/// <summary>
/// Reads a Blu-ray HDMV menu: its interactive graphics stream (PID 0x1400-0x141F in the menu's
/// clip), as libbluray's ig_decode.c does: the pages, their buttons with their place on screen and
/// navigation commands. Java (BD-J) menus have no such stream.
/// </summary>
public static class IgMenu
{
    private const int PacketSize = 192; // a Blu-ray transport packet: 4-byte time stamp + 188
    private const int SegmentIcs = 0x18;

    /// <summary>The buttons of the first whole menu found in the first <paramref name="maxBytes"/> of a clip.</summary>
    public static List<MenuButton> Read(Stream m2ts, long maxBytes)
    {
        var pes = new Dictionary<int, List<byte>>();
        var ics = new List<byte>();
        var packet = new byte[PacketSize];
        long read = 0;
        while (read < maxBytes && Fill(m2ts, packet))
        {
            read += PacketSize;
            if (packet[4] != 0x47)
            {
                continue;
            }

            var pid = (packet[5] & 0x1F) << 8 | packet[6];
            if (pid is < 0x1400 or > 0x141F)
            {
                continue;
            }

            var start = (packet[5] & 0x40) != 0;
            var adaptation = (packet[7] >> 4) & 0x03;
            var offset = 8;
            if (adaptation is 2 or 3)
            {
                offset += 1 + packet[8];
            }

            if (adaptation is 0 or 2 || offset >= PacketSize)
            {
                continue;
            }

            if (start && pes.Remove(pid, out var done) && Segments(done, ics) is { } menu)
            {
                return menu;
            }

            if (start)
            {
                pes[pid] = [];
            }

            if (pes.TryGetValue(pid, out var buffer))
            {
                buffer.AddRange(packet.AsSpan(offset));
            }
        }

        foreach (var left in pes.Values)
        {
            if (Segments(left, ics) is { } menu)
            {
                return menu;
            }
        }

        return [];
    }

    private static bool Fill(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
            {
                return false;
            }

            total += n;
        }

        return true;
    }

    // A PES packet's segments; ICS fragments are joined until the last one, then parsed.
    private static List<MenuButton>? Segments(List<byte> packet, List<byte> ics)
    {
        var data = packet.ToArray();
        if (data.Length < 9 || data[0] != 0 || data[1] != 0 || data[2] != 1)
        {
            return null;
        }

        var pos = 9 + data[8]; // PES header
        while (pos + 3 <= data.Length)
        {
            var type = data[pos];
            var length = data[pos + 1] << 8 | data[pos + 2];
            var body = pos + 3;
            if (body + length > data.Length)
            {
                break;
            }

            if (type == SegmentIcs && length >= 9)
            {
                var sequence = data[body + 8];
                var first = (sequence & 0x80) != 0;
                var last = (sequence & 0x40) != 0;
                if (first)
                {
                    ics.Clear();
                    // video (5), composition (3), sequence (1) descriptors, then the data length (3).
                    ics.AddRange(data.AsSpan(body + 12, Math.Max(0, length - 12)));
                }
                else if (ics.Count > 0)
                {
                    ics.AddRange(data.AsSpan(body + 9, length - 9));
                }

                if (last && ics.Count > 0)
                {
                    try
                    {
                        return ParseComposition(ics.ToArray());
                    }
                    catch (IndexOutOfRangeException)
                    {
                        ics.Clear(); // a broken one: wait for the next copy (menus repeat it)
                    }
                }
            }

            pos = body + length;
        }

        return null;
    }

    /// <summary>The interactive composition: its pages, button overlap groups and buttons.</summary>
    public static List<MenuButton> ParseComposition(byte[] d)
    {
        var p = 0;
        var streamModel = d[p++] >> 7;
        if (streamModel == 0)
        {
            p += 10; // composition and selection time-out PTS
        }

        p += 3; // user time-out
        var pages = d[p++];
        var buttons = new List<MenuButton>();
        for (var page = 0; page < pages; page++)
        {
            var id = d[p];
            p += 2 + 8; // id, version, UO mask
            p = SkipEffects(d, p);
            p = SkipEffects(d, p);
            p += 1 + 2 + 2 + 1; // frame rate, default selected and activated buttons, palette
            var groups = d[p++];
            for (var g = 0; g < groups; g++)
            {
                p += 2; // default valid button
                var count = d[p++];
                for (var b = 0; b < count; b++)
                {
                    var buttonId = U16(d, p);
                    var numeric = U16(d, p + 2);
                    var x = U16(d, p + 5);
                    var y = U16(d, p + 7);
                    p += 2 + 2 + 1 + 4 + 8; // id, numeric select value, auto action, x, y, neighbours
                    p += 5 + 6 + 5; // normal, selected, activated states
                    var commands = U16(d, p);
                    p += 2;
                    var list = new List<HdmvCommand>(commands);
                    for (var c = 0; c < commands; c++)
                    {
                        list.Add(HdmvCommand.Parse(d.AsSpan(p, 12)));
                        p += 12;
                    }

                    buttons.Add(new MenuButton(id, buttonId, numeric, x, y, list));
                }
            }
        }

        return buttons;
    }

    private static int SkipEffects(byte[] d, int p)
    {
        var windows = d[p++];
        p += windows * 9;
        var effects = d[p++];
        for (var e = 0; e < effects; e++)
        {
            p += 3 + 1; // duration, palette
            var objects = d[p++];
            for (var o = 0; o < objects; o++)
            {
                var cropped = (d[p + 3] & 0x80) != 0;
                p += 2 + 1 + 1 + 4 + (cropped ? 8 : 0);
            }
        }

        return p;
    }

    private static int U16(byte[] d, int p) => d[p] << 8 | d[p + 1];

    /// <summary>
    /// The episodes in the order the menu shows them: on the page whose buttons play most of them,
    /// by the numbers the remote selects them with when the buttons have them, else top to bottom
    /// and left to right. Null when the menu plays fewer than two of them.
    /// </summary>
    public static List<string>? Order(IReadOnlyList<MenuButton> buttons, IReadOnlyCollection<string> playlists)
    {
        var wanted = playlists.ToDictionary(n => uint.TryParse(n, out var v) ? v : uint.MaxValue, n => n);
        var hits = buttons
            .Select(b => (Button: b, Playlist: b.PlaylistNumbers.Where(wanted.ContainsKey).Select(v => wanted[v]).FirstOrDefault()))
            .Where(x => x.Playlist != null)
            .ToList();
        var page = hits.GroupBy(x => x.Button.Page)
            .OrderByDescending(g => g.Select(x => x.Playlist).Distinct().Count())
            .ThenBy(g => g.Key)
            .FirstOrDefault()?.ToList();
        if (page == null || page.Select(x => x.Playlist).Distinct().Count() < 2)
        {
            return null;
        }

        var numbered = page.All(x => x.Button.NumericSelect is > 0 and < 0xFFFF)
                       && page.Select(x => x.Button.NumericSelect).Distinct().Count() == page.Count;
        var ordered = numbered
            ? page.OrderBy(x => x.Button.NumericSelect)
            : page.OrderBy(x => x.Button.Y).ThenBy(x => x.Button.X);
        return ordered.Select(x => x.Playlist!).Distinct().ToList();
    }
}
