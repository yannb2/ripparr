using System.Globalization;
using System.Text.RegularExpressions;

namespace Ripparr.Disc;

/// <summary>
/// A DVD's titles as Ripparr's playlists, so the disc page, the TV episodes card and the jobs
/// treat them like a Blu-ray's: each title becomes a playlist named "title03" whose play items
/// are its cells (shared cells mean a "play all" or a duplicate, as shared clips do on a
/// Blu-ray) and whose streams are the title's, numbered by their VOB stream ids (0xE0 the video,
/// 0x80 the first AC-3 track, 0x20 the first subtitles).
/// A part of a title ("title01-c07-12": chapters 7 to 12) is a playlist too: TV DVDs often play
/// several episodes as one title, one after the other, with the episodes as chapters.
/// </summary>
public static partial class DvdDisc
{
    public const string TitlePrefix = "title";

    public static string Name(int title) => TitlePrefix + title.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>"title01-c07-12": chapters 7 to 12 of title 1.</summary>
    public static string PartName(string title, int from, int to) =>
        string.Create(CultureInfo.InvariantCulture, $"{title}-c{from:00}-{to:00}");

    /// <summary>The title, first and last chapter of a part's name, or null for anything else.</summary>
    public static (string Title, int From, int To)? ParsePart(string name) =>
        PartRegex().Match(name) is { Success: true } m
            ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;

    /// <summary>The DVD title number in a playlist name ("title03" or "title03-c01-06" -> 3).</summary>
    public static int? TitleNumber(string name)
    {
        var title = ParsePart(name)?.Title ?? name;
        return title.StartsWith(TitlePrefix, StringComparison.Ordinal)
               && int.TryParse(title.AsSpan(TitlePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>A cell as a play item's clip: its title set and first sector, so two titles playing the same cell share it.</summary>
    public static string CellClip(int titleSet, DvdCell cell) =>
        string.Create(CultureInfo.InvariantCulture, $"VTS{titleSet:00}-{cell.FirstSector:D7}");

    /// <summary>Each title as a playlist, with its cells' sizes for the analysis.</summary>
    public static List<PlaylistInfo> Playlists(IReadOnlyList<DvdTitle> titles, IDictionary<string, long> clipSizes)
    {
        var playlists = new List<PlaylistInfo>();
        foreach (var title in titles)
        {
            foreach (var cell in title.Cells)
            {
                clipSizes[CellClip(title.TitleSet, cell)] = cell.Bytes;
            }

            playlists.Add(new PlaylistInfo { Name = Name(title.Number), Mpls = ToMpls(title), Dvd = title });
        }

        return playlists;
    }

    public static Mpls ToMpls(DvdTitle title)
    {
        var items = title.Cells
            .Select(c => new PlayItem(CellClip(title.TitleSet, c), 0, (uint)Math.Round(c.Duration.TotalSeconds * Mpls.ClockRate), 1))
            .ToList();
        var streams = new List<PlaylistStream>
        {
            new()
            {
                Kind = StreamKind.Video, Coding = title.VideoCoding == "MPEG1" ? (byte)0x01 : (byte)0x02, Pid = 0xE0,
                // Blu-ray's video format codes: 1 = 480i, 2 = 576i; frame rate 4 = 29.97, 3 = 25.
                Format = title.Height > 480 || title.Standard == "PAL" ? (byte)2 : (byte)1,
                Rate = title.Standard == "PAL" ? (byte)3 : (byte)4
            }
        };
        streams.AddRange(title.Audio.Select(a => new PlaylistStream
        {
            Kind = StreamKind.Audio,
            Coding = a.Coding switch
            {
                "DTS" => 0x82,
                "LPCM" => 0x80,
                "MPEG" => 0x03,
                _ => 0x81
            },
            Pid = a.StreamId,
            Language = a.Language,
            // Blu-ray's presentation codes: 1 mono, 3 stereo, 6 multichannel; rate 1 = 48 kHz, 4 = 96 kHz.
            Format = a.Channels switch
            {
                1 => 1,
                2 => 3,
                _ => 6
            },
            Rate = a.SampleRate == 96000 ? (byte)4 : (byte)1,
            Commentary = a.Commentary,
            VisuallyImpaired = a.VisuallyImpaired
        }));
        streams.AddRange(title.Subtitles.Select(s => new PlaylistStream
        {
            Kind = StreamKind.Subtitle,
            Coding = StreamCoding.VobSub,
            Pid = s.StreamId,
            Language = s.Language,
            Forced = s.Forced,
            HearingImpaired = s.Captions,
            Commentary = s.Commentary
        }));
        return new Mpls
        {
            Version = "DVD",
            Items = items,
            Streams = streams,
            Chapters = title.Chapters.Select(c => new ChapterMark(c)).ToList()
        };
    }

    /// <summary>
    /// Chapters <paramref name="from"/> to <paramref name="to"/> (from 1) of a title as a
    /// playlist of its own: the cells in that stretch, the chapters moved to start at 0.
    /// Null when the chapters are not on the title.
    /// </summary>
    public static PlaylistInfo? Part(PlaylistInfo title, int from, int to)
    {
        var chapters = title.Mpls.Chapters;
        if (title.Dvd == null || from < 1 || to < from || to > chapters.Count)
        {
            return null;
        }

        var start = chapters[from - 1].Start;
        var end = to < chapters.Count ? chapters[to].Start : title.Duration;
        var items = new List<PlayItem>();
        var at = TimeSpan.Zero;
        foreach (var item in title.Mpls.Items)
        {
            var length = TimeSpan.FromSeconds(item.Duration45k / (double)Mpls.ClockRate);
            // Chapters start on cells: a cell is in the part when its middle is. The chapters
            // can be a little off the cells' starts (a rip's first chapter at 0.067 s).
            var middle = at + length / 2;
            if (middle >= start && middle < end)
            {
                items.Add(item);
            }

            at += length;
        }

        if (items.Count == 0)
        {
            return null;
        }

        var mpls = new Mpls
        {
            Version = title.Mpls.Version,
            Items = items,
            Streams = title.Mpls.Streams,
            Chapters = chapters.Skip(from - 1).Take(to - from + 1).Select(c => new ChapterMark(c.Start - start)).ToList()
        };
        return new PlaylistInfo
        {
            Name = PartName(title.Name, from, to), Mpls = mpls, Dvd = title.Dvd, PartOf = title.Name, FirstChapter = from, LastChapter = to
        };
    }

    /// <summary>
    /// Where the episodes of a long title start, as chapter numbers (1 is always first), for
    /// <paramref name="episodes"/> episodes: the chapter nearest to each even share of the length.
    /// </summary>
    public static List<int> EvenSplit(IReadOnlyList<ChapterMark> chapters, TimeSpan duration, int episodes)
    {
        var starts = new List<int> { 1 };
        if (episodes < 2 || chapters.Count < 2)
        {
            return starts;
        }

        for (var k = 1; k < episodes; k++)
        {
            var wanted = duration * k / episodes;
            var best = Enumerable.Range(2, chapters.Count - 1)
                .Where(n => n > starts[^1])
                .OrderBy(n => Math.Abs((chapters[n - 1].Start - wanted).TotalSeconds))
                .FirstOrDefault();
            if (best > 0 && !starts.Contains(best))
            {
                starts.Add(best);
            }
        }

        return starts.Order().ToList();
    }

    /// <summary>
    /// A guess at how many episodes a long title holds: the number whose even split gives parts
    /// of the most even length, each 15 minutes or more. 1 when no split looks right.
    /// </summary>
    public static int GuessEpisodes(IReadOnlyList<ChapterMark> chapters, TimeSpan duration)
    {
        var best = (Episodes: 1, Spread: double.MaxValue);
        for (var n = 2; n <= Math.Min(chapters.Count, 12); n++)
        {
            var lengths = Lengths(chapters, duration, EvenSplit(chapters, duration, n));
            if (lengths.Count != n || lengths.Min() < TimeSpan.FromMinutes(15))
            {
                continue;
            }

            var spread = (lengths.Max() - lengths.Min()).TotalSeconds / lengths.Average(l => l.TotalSeconds);
            if (spread < 0.15 && spread < best.Spread - 0.02)
            {
                best = (n, spread);
            }
        }

        return best.Episodes;
    }

    /// <summary>The length of each part when the episodes start at these chapters.</summary>
    public static List<TimeSpan> Lengths(IReadOnlyList<ChapterMark> chapters, TimeSpan duration, IReadOnlyList<int> starts)
    {
        var lengths = new List<TimeSpan>();
        for (var i = 0; i < starts.Count; i++)
        {
            var from = chapters[starts[i] - 1].Start;
            var to = i + 1 < starts.Count ? chapters[starts[i + 1] - 1].Start : duration;
            lengths.Add(to - from);
        }

        return lengths;
    }

    /// <summary>"1, 7, 13" -> [1, 7, 13]: sorted, without repeats, within the chapters; 1 is added when missing.</summary>
    public static List<int> ParseStarts(string? text, int chapters)
    {
        var starts = (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n >= 1 && n <= chapters)
            .Append(1)
            .Distinct().Order().ToList();
        return starts;
    }

    /// <summary>The parts' chapter ranges for these starts: (1, 6), (7, 12), (13, 18).</summary>
    public static List<(int From, int To)> Ranges(IReadOnlyList<int> starts, int chapters) =>
        starts.Select((s, i) => (s, i + 1 < starts.Count ? starts[i + 1] - 1 : chapters)).ToList();

    [GeneratedRegex(@"^(title\d+)-c(\d+)-(\d+)$")]
    private static partial Regex PartRegex();
}
