using System.Buffers.Binary;

namespace Ripparr.Subtitles;

/// <summary>One picture object of a caption, still run-length encoded, where it goes on the screen.</summary>
public sealed record PgsObject(int X, int Y, int Width, int Height, byte[] Rle, (int X, int Y, int Width, int Height)? Crop);

/// <summary>
/// One caption of a PGS (Blu-ray) subtitle stream: when it shows, where, and its pictures with
/// the palette in force. The pictures stay encoded until <see cref="Render"/> is called, so a
/// track of 2,000 captions takes a few megabytes.
/// </summary>
public sealed class PgsCaption
{
    public int Index { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; set; }
    public bool Forced { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public required IReadOnlyList<PgsObject> Objects { get; init; }

    /// <summary>RGBA for each of the 256 palette entries.</summary>
    public required uint[] Palette { get; init; }

    /// <summary>The box all the objects fit in, in screen pixels.</summary>
    public (int X, int Y, int Width, int Height) Bounds
    {
        get
        {
            if (Objects.Count == 0)
            {
                return (0, 0, 0, 0);
            }

            var left = Objects.Min(o => o.X);
            var top = Objects.Min(o => o.Y);
            var right = Objects.Max(o => o.X + (o.Crop?.Width ?? o.Width));
            var bottom = Objects.Max(o => o.Y + (o.Crop?.Height ?? o.Height));
            return (left, top, right - left, bottom - top);
        }
    }

    /// <summary>Decodes the pictures into one RGBA image the size of <see cref="Bounds"/>.</summary>
    public RgbaImage Render()
    {
        var (left, top, width, height) = Bounds;
        var image = new RgbaImage(Math.Max(width, 1), Math.Max(height, 1));
        foreach (var obj in Objects)
        {
            var pixels = PgsReader.DecodeRle(obj.Rle, obj.Width, obj.Height);
            var crop = obj.Crop ?? (0, 0, obj.Width, obj.Height);
            for (var y = 0; y < crop.Height; y++)
            {
                var sy = crop.Y + y;
                var dy = obj.Y - top + y;
                if (sy >= obj.Height || dy < 0 || dy >= image.Height)
                {
                    continue;
                }

                for (var x = 0; x < crop.Width; x++)
                {
                    var sx = crop.X + x;
                    var dx = obj.X - left + x;
                    if (sx >= obj.Width || dx < 0 || dx >= image.Width)
                    {
                        continue;
                    }

                    var color = Palette[pixels[sy * obj.Width + sx]];
                    if ((color & 0xFF) != 0)
                    {
                        image.Pixels[dy * image.Width + dx] = color;
                    }
                }
            }
        }

        return image;
    }
}

/// <summary>Pixels as 0xRRGGBBAA.</summary>
public sealed class RgbaImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = new uint[width * height];
}

/// <summary>
/// Reads a .sup file (PGS, the Blu-ray subtitle format DGDemux writes): segments of palette
/// (PDS), picture (ODS), window (WDS) and composition (PCS), grouped into display sets that
/// each end with an END segment. A display set with pictures starts a caption; the next
/// display set ends it.
/// </summary>
public static class PgsReader
{
    private const byte PaletteSegment = 0x14;
    private const byte ObjectSegment = 0x15;
    private const byte CompositionSegment = 0x16;
    private const byte EndSegment = 0x80;

    public static List<PgsCaption> Read(string path) => Read(File.ReadAllBytes(path));

    public static List<PgsCaption> Read(ReadOnlySpan<byte> data)
    {
        var captions = new List<PgsCaption>();
        var palettes = new Dictionary<int, uint[]>();
        var objects = new Dictionary<int, (int Width, int Height, MemoryStream Data)>();
        Composition? composition = null;
        PgsCaption? open = null;
        var changed = false; // a palette or picture came in this display set
        var position = 0;

        while (position + 13 <= data.Length)
        {
            if (data[position] != 'P' || data[position + 1] != 'G')
            {
                position++; // not a segment start: resynchronise
                continue;
            }

            var pts = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 2)..]);
            var type = data[position + 10];
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 11)..]);
            var bodyStart = position + 13;
            if (bodyStart + size > data.Length)
            {
                break;
            }

            var body = data.Slice(bodyStart, size);
            position = bodyStart + size;

            switch (type)
            {
                case CompositionSegment when body.Length >= 11:
                    composition = Composition.Parse(body, pts);
                    if (composition.EpochStart)
                    {
                        palettes.Clear();
                        objects.Clear();
                    }

                    break;
                case PaletteSegment when body.Length >= 2:
                    var id = body[0];
                    if (!palettes.TryGetValue(id, out var palette))
                    {
                        palettes[id] = palette = new uint[256];
                    }

                    changed = true;
                    for (var i = 2; i + 5 <= body.Length; i += 5)
                    {
                        palette[body[i]] = ToRgba(body[i + 1], body[i + 2], body[i + 3], body[i + 4]);
                    }

                    break;
                case ObjectSegment when body.Length >= 4:
                    var objectId = BinaryPrimitives.ReadUInt16BigEndian(body);
                    var first = (body[3] & 0x80) != 0;
                    changed = true;
                    if (first && body.Length >= 11)
                    {
                        var width = BinaryPrimitives.ReadUInt16BigEndian(body[7..]);
                        var height = BinaryPrimitives.ReadUInt16BigEndian(body[9..]);
                        var stream = new MemoryStream();
                        stream.Write(body[11..]);
                        objects[objectId] = (width, height, stream);
                    }
                    else if (!first && objects.TryGetValue(objectId, out var existing))
                    {
                        existing.Data.Write(body[4..]);
                    }

                    break;
                case EndSegment when composition != null:
                    var start = TimeSpan.FromMilliseconds(Math.Round(composition.Pts / 90.0)); // 90 kHz clock
                    var repeat = !changed && open != null && composition.Objects.Count == open.Objects.Count
                                 && composition.Objects.Zip(open.Objects).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y);
                    changed = false;
                    if (repeat)
                    {
                        // An acquisition point repeating the caption on screen: it goes on.
                        composition = null;
                        break;
                    }

                    if (open != null && open.End == TimeSpan.Zero)
                    {
                        open.End = start;
                    }

                    open = null;
                    var placed = composition.Objects
                        .Where(o => objects.ContainsKey(o.ObjectId))
                        .Select(o =>
                        {
                            var source = objects[o.ObjectId];
                            return new PgsObject(o.X, o.Y, source.Width, source.Height, source.Data.ToArray(), o.Crop);
                        })
                        .ToList();
                    if (placed.Count > 0)
                    {
                        open = new PgsCaption
                        {
                            Index = captions.Count + 1,
                            Start = start,
                            Forced = composition.Objects.Any(o => o.Forced),
                            ScreenWidth = composition.Width,
                            ScreenHeight = composition.Height,
                            Objects = placed,
                            Palette = palettes.TryGetValue(composition.PaletteId, out var p) ? (uint[])p.Clone() : new uint[256]
                        };
                        captions.Add(open);
                    }

                    composition = null;
                    break;
            }
        }

        // A last caption with no end: show it for a few seconds.
        if (open != null && open.End == TimeSpan.Zero)
        {
            open.End = open.Start + TimeSpan.FromSeconds(4);
        }

        return captions;
    }

    /// <summary>
    /// PGS run-length decoding into palette indexes. A non-zero byte is one pixel of that colour;
    /// 0x00 starts a run: 00 = end of line, 00LLLLLL / 01LLLLLL LLLLLLLL = colour 0,
    /// 10LLLLLL CC / 11LLLLLL LLLLLLLL CC = colour CC.
    /// </summary>
    public static byte[] DecodeRle(ReadOnlySpan<byte> rle, int width, int height)
    {
        var pixels = new byte[width * height];
        int x = 0, y = 0, i = 0;
        while (i < rle.Length && y < height)
        {
            var b = rle[i++];
            int run;
            byte color;
            if (b != 0)
            {
                run = 1;
                color = b;
            }
            else
            {
                if (i >= rle.Length)
                {
                    break;
                }

                var flags = rle[i++];
                if (flags == 0)
                {
                    x = 0;
                    y++;
                    continue;
                }

                run = flags & 0x3F;
                if ((flags & 0x40) != 0)
                {
                    if (i >= rle.Length)
                    {
                        break;
                    }

                    run = (run << 8) | rle[i++];
                }

                if ((flags & 0x80) != 0)
                {
                    if (i >= rle.Length)
                    {
                        break;
                    }

                    color = rle[i++];
                }
                else
                {
                    color = 0;
                }
            }

            var end = Math.Min(x + run, width);
            if (color != 0)
            {
                pixels.AsSpan(y * width + x, end - x).Fill(color);
            }

            x = end;
        }

        return pixels;
    }

    /// <summary>BT.709 Y'CbCr (as on HD and UHD discs) to 0xRRGGBBAA.</summary>
    public static uint ToRgba(byte y, byte cr, byte cb, byte alpha)
    {
        var luma = 1.164 * (y - 16);
        var r = Clamp(luma + 1.793 * (cr - 128));
        var g = Clamp(luma - 0.213 * (cb - 128) - 0.533 * (cr - 128));
        var b = Clamp(luma + 2.112 * (cb - 128));
        return (uint)(r << 24 | g << 16 | b << 8 | alpha);

        static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
    }

    private sealed record CompositionObject(int ObjectId, int X, int Y, bool Forced, (int X, int Y, int Width, int Height)? Crop);

    private sealed class Composition
    {
        public uint Pts { get; private init; }
        public int Width { get; private init; }
        public int Height { get; private init; }
        public bool EpochStart { get; private init; }
        public int PaletteId { get; private init; }
        public List<CompositionObject> Objects { get; } = [];

        public static Composition Parse(ReadOnlySpan<byte> body, uint pts)
        {
            var composition = new Composition
            {
                Pts = pts,
                Width = BinaryPrimitives.ReadUInt16BigEndian(body),
                Height = BinaryPrimitives.ReadUInt16BigEndian(body[2..]),
                EpochStart = (body[7] & 0x80) != 0,
                PaletteId = body[9]
            };
            var count = body[10];
            var at = 11;
            for (var n = 0; n < count && at + 8 <= body.Length; n++)
            {
                var objectId = BinaryPrimitives.ReadUInt16BigEndian(body[at..]);
                var flags = body[at + 3];
                var x = BinaryPrimitives.ReadUInt16BigEndian(body[(at + 4)..]);
                var y = BinaryPrimitives.ReadUInt16BigEndian(body[(at + 6)..]);
                at += 8;
                (int, int, int, int)? crop = null;
                if ((flags & 0x80) != 0 && at + 8 <= body.Length)
                {
                    crop = (BinaryPrimitives.ReadUInt16BigEndian(body[at..]), BinaryPrimitives.ReadUInt16BigEndian(body[(at + 2)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(body[(at + 4)..]), BinaryPrimitives.ReadUInt16BigEndian(body[(at + 6)..]));
                    at += 8;
                }

                composition.Objects.Add(new CompositionObject(objectId, x, y, (flags & 0x40) != 0, crop));
            }

            return composition;
        }
    }
}
