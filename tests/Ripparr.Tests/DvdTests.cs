using Ripparr.Disc;
using Ripparr.Profiles;
using Ripparr.Tools;

namespace Ripparr.Tests;

/// <summary>
/// Samples/dvd is a small DVD made with dvdauthor: title 1 plays three 40 s "episodes" in a row
/// (chapters at 0/10/20/30 s, 0/20 s and 0/15/30 s: 9 chapters), title 2 is the first episode
/// again (2 chapters), title 3 is in a second title set (4:3, Japanese audio, no subtitles).
/// Titles 1 and 2 have English 5.1 and French 2.0 AC-3 and English and French subtitles.
/// lsdvd reads the same lengths and chapters.
/// </summary>
[TestClass]
public class DvdTests
{
    private static string VideoTs => Path.Combine(AppContext.BaseDirectory, "Samples", "dvd", "VIDEO_TS");

    [TestMethod]
    public void ReadsTitlesChaptersAndStreams()
    {
        var titles = DvdIfo.Read(VideoTs);

        Assert.HasCount(3, titles);
        var first = titles[0];
        Assert.AreEqual(1, first.TitleSet);
        Assert.AreEqual(119.9, first.Duration.TotalSeconds, 0.05);
        Assert.HasCount(9, first.Chapters);
        Assert.AreEqual(TimeSpan.Zero, first.Chapters[0]);
        Assert.AreEqual(10.2, first.Chapters[1].TotalSeconds, 0.05);
        Assert.AreEqual(40.0, first.Chapters[4].TotalSeconds, 0.1);
        Assert.AreEqual("16:9", first.Aspect);
        Assert.AreEqual("NTSC", first.Standard);
        Assert.AreEqual(480, first.Height);

        Assert.HasCount(2, first.Audio);
        Assert.AreEqual("eng", first.Audio[0].Language);
        Assert.AreEqual(6, first.Audio[0].Channels);
        Assert.AreEqual(0x80, first.Audio[0].StreamId);
        Assert.AreEqual("fre", first.Audio[1].Language);
        Assert.AreEqual(2, first.Audio[1].Channels);
        Assert.HasCount(2, first.Subtitles);
        Assert.AreEqual("fre", first.Subtitles[1].Language);
        Assert.AreEqual(0x21, first.Subtitles[1].StreamId);

        Assert.HasCount(2, titles[1].Chapters);
        Assert.AreEqual(39.97, titles[1].Duration.TotalSeconds, 0.05);
        Assert.AreEqual(2, titles[2].TitleSet);
        Assert.AreEqual("4:3", titles[2].Aspect);
        Assert.AreEqual("jpn", titles[2].Audio[0].Language);
        Assert.IsEmpty(titles[2].Subtitles);
    }

    private static DiscAnalysis Analysis()
    {
        var analysis = new DiscAnalysis { Source = new DiscSource("/dvd", SourceKind.Folder, "/", 0, DateTime.UtcNow) { Dvd = true } };
        DiscAnalyzer.ReadDvd(analysis, VideoTs);
        analysis.Classify(TimeSpan.FromSeconds(20));
        return analysis;
    }

    [TestMethod]
    public void TitlesArePlaylistsWithTheirStreams()
    {
        var analysis = Analysis();

        Assert.IsTrue(analysis.IsDvd);
        Assert.AreEqual("title01", analysis.MainFeature?.Name);
        var title = analysis.Find("title01")!;
        Assert.AreEqual("Title 1", title.Label);
        Assert.HasCount(9, title.Mpls.Items);
        Assert.IsGreaterThan(18_000_000L, title.Size);
        Assert.IsFalse(title.TooLittleData);
        var streams = title.Mpls.Streams;
        Assert.AreEqual("MPEG2", streams[0].CodecName);
        Assert.AreEqual("480i", StreamCoding.Resolution(streams[0].Format));
        Assert.AreEqual("AC3", streams[1].CodecName);
        Assert.AreEqual("6", streams[1].Format.ToString());
        Assert.AreEqual(StreamKind.Subtitle, streams[3].Kind);
        Assert.AreEqual("VobSub", streams[3].CodecName);
        Assert.AreEqual(0x20, streams[3].Pid);
    }

    [TestMethod]
    public void PartsAreMadeFromTheTitlesChapters()
    {
        var analysis = Analysis();

        var part = analysis.Find("title01-c05-06")!;
        Assert.IsNotNull(part);
        Assert.AreEqual("title01", part.PartOf);
        Assert.AreEqual("Title 1, ch. 5-6", part.Label);
        Assert.AreEqual(40.0, part.Duration.TotalSeconds, 0.1);
        Assert.HasCount(2, part.Mpls.Chapters);
        Assert.AreEqual(TimeSpan.Zero, part.Mpls.Chapters[0].Start);
        Assert.AreEqual(19.8, part.Mpls.Chapters[1].Start.TotalSeconds, 0.05);
        Assert.IsGreaterThan(0L, part.Size);
        Assert.AreSame(part, analysis.Find("title01-c05-06"));

        // Not a playlist of the disc: the main feature and the episodes leave it out.
        analysis.Classify(TimeSpan.FromSeconds(20));
        Assert.AreEqual("title01", analysis.MainFeature?.Name);
        Assert.IsNull(analysis.Find("title01-c09-12"));
        Assert.IsNull(analysis.Find("title09-c01-02"));
    }

    [TestMethod]
    public void SplitsALongTitleIntoEpisodes()
    {
        var chapters = new[] { 0, 5, 10, 15, 22, 27, 32, 37, 44, 49, 54, 59 }.Select(m => new ChapterMark(TimeSpan.FromMinutes(m))).ToList();
        var length = TimeSpan.FromMinutes(66);

        Assert.AreEqual(3, DvdDisc.GuessEpisodes(chapters, length));
        var starts = DvdDisc.EvenSplit(chapters, length, 3);
        CollectionAssert.AreEqual(new[] { 1, 5, 9 }, starts);
        CollectionAssert.AreEqual(new[] { (1, 4), (5, 8), (9, 12) }, DvdDisc.Ranges(starts, chapters.Count));
        CollectionAssert.AreEqual(new[] { 1, 5, 9 }, DvdDisc.ParseStarts("9, 5 5 40", chapters.Count));
        Assert.AreEqual(1, DvdDisc.GuessEpisodes(chapters.Take(3).ToList(), TimeSpan.FromMinutes(14)));
    }

    [TestMethod]
    public void NamesOfTitlesAndParts()
    {
        Assert.AreEqual("title03", DvdDisc.Name(3));
        Assert.AreEqual("title03-c07-12", DvdDisc.PartName("title03", 7, 12));
        Assert.AreEqual(("title03", 7, 12), DvdDisc.ParsePart("title03-c07-12"));
        Assert.IsNull(DvdDisc.ParsePart("00800"));
        Assert.AreEqual(3, DvdDisc.TitleNumber("title03-c07-12"));
        Assert.AreEqual(12, DvdDisc.TitleNumber("title12"));
        Assert.IsNull(DvdDisc.TitleNumber("00800"));
    }

    [TestMethod]
    public void FindsDvdFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ripparr-dvd-{Guid.NewGuid():N}");
        var disc = Path.Combine(root, "Some Show S01D1");
        Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS"));
        File.Copy(Path.Combine(VideoTs, "VIDEO_TS.IFO"), Path.Combine(disc, "VIDEO_TS", "VIDEO_TS.IFO"));
        try
        {
            var found = SourceFinder.Find([new Options.SourceRoot { Path = root }], []);

            Assert.HasCount(1, found);
            Assert.IsTrue(found[0].Dvd);
            Assert.AreEqual(disc, found[0].Path);
            Assert.AreEqual(disc, SourceFinder.DiscParent(Path.Combine(disc, "VIDEO_TS")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DvdFlagsGoOnThePlan()
    {
        var mpls = new Mpls
        {
            Version = "DVD",
            Items = [new PlayItem("VTS01-0000000", 0, 45000 * 60, 1)],
            Chapters = [],
            Streams =
            [
                new PlaylistStream { Kind = StreamKind.Video, Coding = 0x02, Pid = 0xE0 },
                new PlaylistStream { Kind = StreamKind.Audio, Coding = 0x81, Pid = 0x80, Language = "eng", Format = 6 },
                new PlaylistStream { Kind = StreamKind.Audio, Coding = 0x81, Pid = 0x81, Language = "eng", Format = 3, Commentary = true },
                new PlaylistStream { Kind = StreamKind.Subtitle, Coding = StreamCoding.VobSub, Pid = 0x20, Language = "eng", Forced = true },
                new PlaylistStream { Kind = StreamKind.Subtitle, Coding = StreamCoding.VobSub, Pid = 0x21, Language = "eng", HearingImpaired = true }
            ]
        };

        var plan = TrackPlanner.Build(mpls, new RipProfile(), "eng");

        var commentary = plan.Tracks.Single(t => t.Pid == 0x81);
        Assert.IsTrue(commentary.Commentary);
        Assert.IsFalse(commentary.Unsure);
        Assert.IsTrue(plan.Tracks.Single(t => t.Pid == 0x20).Forced);
        Assert.IsTrue(plan.Tracks.Single(t => t.Pid == 0x21).HearingImpaired);
        Assert.AreEqual("VobSub", plan.Tracks.Single(t => t.Pid == 0x21).Codec);
    }

    [TestMethod]
    public void MatchesMakeMkvTitlesAFewSecondsOff()
    {
        var wanted = new DvdTitle { Number = 1, Chapters = Enumerable.Range(0, 6).Select(i => TimeSpan.FromMinutes(i * 12)).ToList(), Cells = [new DvdCell(new TimeSpan(1, 15, 48), 0, 1)] };
        var other = new DvdTitle { Number = 2, Chapters = [TimeSpan.Zero], Cells = [new DvdCell(TimeSpan.FromMinutes(3), 2, 3)] };
        var all = new[] { wanted, other };

        // Record of Lodoss War: the IFO says 1:15:48, MakeMKV lists 1:15:46.
        var makeMkv = new[] { new MakeMkv.Title(0, new TimeSpan(1, 15, 46), 6, null, null, 0) };
        Assert.AreEqual(0, MakeMkv.DvdTitle(makeMkv, wanted, all)?.Id);

        // The original title number wins when the length is close.
        var numbered = new[] { new MakeMkv.Title(0, new TimeSpan(1, 15, 30), 6, 2, null, 0), new MakeMkv.Title(1, new TimeSpan(1, 15, 10), 6, 1, null, 0) };
        Assert.AreEqual(1, MakeMkv.DvdTitle(numbered, wanted, all)?.Id);

        Assert.IsNull(MakeMkv.DvdTitle([new MakeMkv.Title(0, TimeSpan.FromMinutes(60), 6, null, null, 0)], wanted, all));
    }

    [TestMethod]
    public void PartsKeepTheirCellsWhenTheRipsChaptersAreALittleOff()
    {
        // MakeMKV's rip can start chapter 1 at 0.067 s and the others a second or two off the
        // IFO's cells: the part must still hold the same cells (Record of Lodoss War).
        var title = Analysis().Find("title01")!;
        var shifted = title.Mpls.Chapters.Select((c, i) => new ChapterMark(c.Start + TimeSpan.FromMilliseconds(i == 0 ? 67 : 1800))).ToList();
        var ripped = title.WithMpls(new Mpls { Version = title.Mpls.Version, Items = title.Mpls.Items, Streams = title.Mpls.Streams, Chapters = shifted });

        var first = DvdDisc.Part(ripped, 1, 4)!;
        Assert.HasCount(4, first.Mpls.Items);
        Assert.AreEqual(40.0, first.Duration.TotalSeconds, 0.1);
        Assert.HasCount(2, DvdDisc.Part(ripped, 5, 6)!.Mpls.Items);
    }
}
