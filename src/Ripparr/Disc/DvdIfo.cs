using System.Buffers.Binary;
using System.Text;

namespace Ripparr.Disc;

/// <summary>An audio stream of a DVD title set, as its VTS_nn_0.IFO describes it.</summary>
public sealed record DvdAudio(int Number, string Coding, int Channels, int SampleRate, string? Language, int Extension)
{
    /// <summary>The stream id in the VOB: 0x80 AC-3, 0x88 DTS, 0xA0 LPCM, 0xC0 MPEG audio, plus the stream number.</summary>
    public int StreamId => Coding switch
    {
        "DTS" => 0x88 + Number,
        "LPCM" => 0xA0 + Number,
        "MPEG" => 0xC0 + Number,
        _ => 0x80 + Number
    };

    /// <summary>Code extension 3 and 4: director's comments.</summary>
    public bool Commentary => Extension is 3 or 4;

    /// <summary>Code extension 2: for the visually impaired (audio description).</summary>
    public bool VisuallyImpaired => Extension == 2;
}

/// <summary>A subpicture (subtitle) stream of a DVD title set.</summary>
public sealed record DvdSubtitle(int Number, string? Language, int Extension)
{
    /// <summary>The stream id in the VOB: 0x20 plus the stream number.</summary>
    public int StreamId => 0x20 + Number;

    /// <summary>Code extension 9: forced captions.</summary>
    public bool Forced => Extension == 9;

    /// <summary>Code extensions 5 to 7: captions (closed captions, for the deaf and hard of hearing).</summary>
    public bool Captions => Extension is 5 or 6 or 7;

    /// <summary>Code extensions 13 to 15: director's comments.</summary>
    public bool Commentary => Extension is 13 or 14 or 15;
}

/// <summary>A cell the title plays: its length and where its sectors are in the title set's VOBs.</summary>
public sealed record DvdCell(TimeSpan Duration, uint FirstSector, uint LastSector)
{
    public long Bytes => ((long)LastSector - FirstSector + 1) * DvdIfo.SectorSize;
}

/// <summary>A title of the disc (what the menu plays): its cells in order, its chapters and its streams.</summary>
public sealed class DvdTitle
{
    /// <summary>The title number on the disc, from 1, as players and MakeMKV's "original title" count them.</summary>
    public int Number { get; init; }

    public int TitleSet { get; init; }

    public int Angles { get; init; } = 1;

    public List<DvdCell> Cells { get; init; } = [];

    /// <summary>Where each chapter starts, from the start of the title.</summary>
    public List<TimeSpan> Chapters { get; init; } = [];

    /// <summary>MPEG-1 or MPEG-2.</summary>
    public string VideoCoding { get; init; } = "MPEG2";

    /// <summary>NTSC or PAL.</summary>
    public string Standard { get; init; } = "NTSC";

    public int Width { get; init; } = 720;

    public int Height { get; init; } = 480;

    /// <summary>4:3 or 16:9.</summary>
    public string Aspect { get; init; } = "4:3";

    /// <summary>The audio streams the title plays (the PGC's audio control marks which), in stream number order.</summary>
    public List<DvdAudio> Audio { get; init; } = [];

    public List<DvdSubtitle> Subtitles { get; init; } = [];

    public TimeSpan Duration => Cells.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

    public long Bytes => Cells.Sum(c => c.Bytes);
}

/// <summary>
/// Reads a DVD's IFO files (VIDEO_TS.IFO and VTS_nn_0.IFO) as libdvdread and lsdvd do: the
/// titles, their chapters and cells, and the streams. The IFO files are never scrambled (CSS
/// only scrambles the VOBs), so this works on any DVD without MakeMKV.
/// </summary>
public static class DvdIfo
{
    public const int SectorSize = 2048;

    /// <summary>The titles of the disc whose VIDEO_TS folder is given. Throws <see cref="InvalidDataException"/> when the IFOs cannot be read.</summary>
    public static List<DvdTitle> Read(string videoTs)
    {
        var vmg = FileIn(videoTs, "VIDEO_TS.IFO") ?? FileIn(videoTs, "VIDEO_TS.BUP")
                  ?? throw new InvalidDataException("No VIDEO_TS.IFO in the VIDEO_TS folder.");
        var sets = new Dictionary<int, byte[]?>();
        return Parse(File.ReadAllBytes(vmg), number =>
        {
            if (!sets.TryGetValue(number, out var bytes))
            {
                var name = $"VTS_{number:00}_0";
                var file = FileIn(videoTs, name + ".IFO") ?? FileIn(videoTs, name + ".BUP");
                bytes = file == null ? null : File.ReadAllBytes(file);
                sets[number] = bytes;
            }

            return bytes;
        });
    }

    /// <summary>The titles from VIDEO_TS.IFO's bytes, with each title set's IFO given by its number (null when missing).</summary>
    public static List<DvdTitle> Parse(byte[] vmg, Func<int, byte[]?> titleSet)
    {
        if (vmg.Length < 0x100 || Encoding.ASCII.GetString(vmg, 0, 12) != "DVDVIDEO-VMG")
        {
            throw new InvalidDataException("Not a DVD's VIDEO_TS.IFO (no DVDVIDEO-VMG header).");
        }

        var srpt = (int)U32(vmg, 0xC4) * SectorSize;
        Check(vmg, srpt, 8);
        var count = U16(vmg, srpt);
        var titles = new List<DvdTitle>();
        for (var i = 0; i < count; i++)
        {
            var entry = srpt + 8 + i * 12;
            if (entry + 12 > vmg.Length)
            {
                break;
            }

            var angles = Math.Max(1, (int)vmg[entry + 1]);
            var set = vmg[entry + 6];
            var titleInSet = vmg[entry + 7];
            var ifo = titleSet(set);
            if (ifo == null)
            {
                continue;
            }

            try
            {
                titles.Add(ReadTitle(ifo, i + 1, set, titleInSet, angles));
            }
            catch (InvalidDataException)
            {
                // One broken title set (some discs ship dummy ones) does not hide the others.
            }
        }

        return titles;
    }

    private static DvdTitle ReadTitle(byte[] vts, int number, int set, int titleInSet, int angles)
    {
        if (vts.Length < 0x400 || Encoding.ASCII.GetString(vts, 0, 12) != "DVDVIDEO-VTS")
        {
            throw new InvalidDataException($"VTS_{set:00}_0.IFO has no DVDVIDEO-VTS header.");
        }

        // The title's chapters (parts of title): each is a program of a PGC.
        var pttStart = (int)U32(vts, 0xC8) * SectorSize;
        Check(vts, pttStart, 8);
        var titleUnits = U16(vts, pttStart);
        if (titleInSet < 1 || titleInSet > titleUnits)
        {
            throw new InvalidDataException($"Title {number} points at title {titleInSet} of VTS {set}, which has {titleUnits}.");
        }

        var pttEnd = pttStart + (int)U32(vts, pttStart + 4) + 1;
        var unitStart = pttStart + (int)U32(vts, pttStart + 8 + (titleInSet - 1) * 4);
        var unitEnd = titleInSet < titleUnits ? pttStart + (int)U32(vts, pttStart + 8 + titleInSet * 4) : pttEnd;
        var parts = new List<(int Pgc, int Program)>();
        for (var at = unitStart; at + 4 <= Math.Min(unitEnd, vts.Length); at += 4)
        {
            parts.Add((U16(vts, at), U16(vts, at + 2)));
        }

        var pgcit = (int)U32(vts, 0xCC) * SectorSize;
        Check(vts, pgcit, 8);
        var pgcCount = U16(vts, pgcit);

        // The PGCs in the order the chapters use them; each one's cells, and where each program starts.
        var cells = new List<DvdCell>();
        var chapters = new List<TimeSpan>();
        var pgcStarts = new Dictionary<int, (TimeSpan Start, List<TimeSpan> ProgramStarts)>();
        var audioPresent = new HashSet<int>();
        var subtitlePresent = new HashSet<int>();
        foreach (var (pgcNumber, program) in parts)
        {
            if (pgcNumber < 1 || pgcNumber > pgcCount)
            {
                continue;
            }

            if (!pgcStarts.TryGetValue(pgcNumber, out var pgc))
            {
                var at = pgcit + (int)U32(vts, pgcit + 8 + (pgcNumber - 1) * 8 + 4);
                Check(vts, at, 0xEC);
                var start = cells.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);
                var (pgcCells, programStarts) = ReadPgc(vts, at);
                cells.AddRange(pgcCells);
                pgc = (start, programStarts);
                pgcStarts[pgcNumber] = pgc;
                if (audioPresent.Count == 0 && subtitlePresent.Count == 0)
                {
                    for (var a = 0; a < 8; a++)
                    {
                        if ((U16(vts, at + 0x0C + a * 2) & 0x8000) != 0)
                        {
                            audioPresent.Add(a);
                        }
                    }

                    for (var s = 0; s < 32; s++)
                    {
                        if ((U32(vts, at + 0x1C + s * 4) & 0x80000000) != 0)
                        {
                            subtitlePresent.Add(s);
                        }
                    }
                }
            }

            if (program >= 1 && program <= pgc.ProgramStarts.Count)
            {
                chapters.Add(pgc.Start + pgc.ProgramStarts[program - 1]);
            }
        }

        // The title set's own attributes.
        var video = U16(vts, 0x200);
        var pal = ((video >> 12) & 3) == 1;
        var height = pal ? 576 : 480;
        var width = ((video >> 3) & 7) switch
        {
            1 => 704,
            2 => 352,
            3 => 352,
            _ => 720
        };
        if (((video >> 3) & 7) == 3)
        {
            height /= 2;
        }

        var audio = new List<DvdAudio>();
        var audioCount = Math.Min(8, (int)vts[0x203]);
        for (var a = 0; a < audioCount; a++)
        {
            if (audioPresent.Count > 0 && !audioPresent.Contains(a))
            {
                continue;
            }

            var at = 0x204 + a * 8;
            var coding = (vts[at] >> 5) switch
            {
                0 => "AC3",
                2 or 3 => "MPEG",
                4 => "LPCM",
                6 => "DTS",
                _ => "AC3"
            };
            var rate = ((vts[at + 1] >> 4) & 3) == 1 ? 96000 : 48000;
            var channels = (vts[at + 1] & 7) + 1;
            var language = ((vts[at] >> 2) & 3) == 1 ? LanguageCode(vts, at + 2) : null;
            audio.Add(new DvdAudio(a, coding, channels, rate, language, vts[at + 5]));
        }

        var subtitles = new List<DvdSubtitle>();
        var subtitleCount = Math.Min(32, (int)vts[0x255]);
        for (var s = 0; s < subtitleCount; s++)
        {
            if (subtitlePresent.Count > 0 && !subtitlePresent.Contains(s))
            {
                continue;
            }

            var at = 0x256 + s * 6;
            var language = (vts[at] & 3) == 1 ? LanguageCode(vts, at + 2) : null;
            subtitles.Add(new DvdSubtitle(s, language, vts[at + 5]));
        }

        return new DvdTitle
        {
            Number = number,
            TitleSet = set,
            Angles = angles,
            Cells = cells,
            Chapters = chapters,
            VideoCoding = ((video >> 14) & 3) == 0 ? "MPEG1" : "MPEG2",
            Standard = pal ? "PAL" : "NTSC",
            Width = width,
            Height = height,
            Aspect = ((video >> 10) & 3) == 3 ? "16:9" : "4:3",
            Audio = audio,
            Subtitles = subtitles
        };
    }

    /// <summary>A PGC's cells (one angle only) and where each of its programs starts.</summary>
    private static (List<DvdCell> Cells, List<TimeSpan> ProgramStarts) ReadPgc(byte[] vts, int at)
    {
        var programs = vts[at + 2];
        var cellCount = vts[at + 3];
        var programMap = at + U16(vts, at + 0xE6);
        var playback = at + U16(vts, at + 0xE8);
        Check(vts, playback, cellCount * 24);
        Check(vts, programMap, programs);

        var cells = new List<DvdCell>();
        var startOfCell = new List<TimeSpan>(); // by cell number - 1, angle cells included
        var elapsed = TimeSpan.Zero;
        for (var c = 0; c < cellCount; c++)
        {
            var cell = playback + c * 24;
            startOfCell.Add(elapsed);
            var blockMode = vts[cell] >> 6;
            var blockType = (vts[cell] >> 4) & 3;
            // In an angle block only the first angle's cell counts: the others play instead of it.
            if (blockType == 1 && blockMode is 2 or 3)
            {
                continue;
            }

            var duration = Time(vts, cell + 4);
            cells.Add(new DvdCell(duration, U32(vts, cell + 8), U32(vts, cell + 20)));
            elapsed += duration;
        }

        var starts = new List<TimeSpan>();
        for (var p = 0; p < programs; p++)
        {
            var entryCell = vts[programMap + p];
            starts.Add(entryCell >= 1 && entryCell <= startOfCell.Count ? startOfCell[entryCell - 1] : elapsed);
        }

        return (cells, starts);
    }

    /// <summary>
    /// A DVD time: hours, minutes, seconds in BCD, then the frames in BCD with the frame rate in
    /// the top two bits (1 = 25 fps, 3 = 29.97 fps).
    /// </summary>
    public static TimeSpan Time(byte[] data, int at)
    {
        static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        var fps = (data[at + 3] >> 6) == 1 ? 25.0 : 30000 / 1001.0;
        var seconds = Bcd(data[at]) * 3600 + Bcd(data[at + 1]) * 60 + Bcd(data[at + 2]) + Bcd((byte)(data[at + 3] & 0x3F)) / fps;
        return TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
    }

    /// <summary>The two-letter code ("en") as MKV writes it (eng); null when blank.</summary>
    private static string? LanguageCode(byte[] data, int at)
    {
        var code = Encoding.ASCII.GetString(data, at, 2).Trim('\0', ' ').ToLowerInvariant();
        if (code.Length != 2 || !code.All(char.IsAsciiLetterLower))
        {
            return null;
        }

        var three = Media.Languages.Base(code);
        return three.Length == 3 ? Media.Languages.Bibliographic(three) : null;
    }

    private static string? FileIn(string folder, string name) => SourceFinder.FileIn(folder, name);

    private static ushort U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2));

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));

    private static void Check(byte[] data, int at, int length)
    {
        if (at < 0 || length < 0 || at + length > data.Length)
        {
            throw new InvalidDataException("The IFO file is shorter than its tables say.");
        }
    }
}
