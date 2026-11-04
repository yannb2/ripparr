using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class MuxBuilderTests
{
    private static readonly Mpls Film = Mpls.Parse(MplsBuilder.UhdFilm().Build());

    private static DemuxedFile Video(string name, int width, bool dv = false) =>
        new($"/w/{name}", new StreamFacts
        {
            Kind = StreamKind.Video, Format = "HEVC", Width = width, Height = width * 9 / 16,
            HdrFormat = dv ? "Dolby Vision / SMPTE ST 2086" : "SMPTE ST 2086", HdrCompatibility = "HDR10",
            HdrProfile = dv ? "dvhe.07.06" : null
        });

    private static DemuxedFile Audio(string name, string format, int channels, string? commercial = null) =>
        new($"/w/{name}", new StreamFacts
        {
            Kind = StreamKind.Audio, Format = format, Channels = channels, Commercial = commercial,
            ChannelLayout = channels > 2 ? "L R C LFE Ls Rs Lb Rb" : "L R"
        });

    private static DemuxedFile Subtitle(string name) => new($"/w/{name}", new StreamFacts { Kind = StreamKind.Subtitle, Format = "PGS" });

    /// <summary>The form's answer to "commentary or AD?" for the stereo AC3: commentary.</summary>
    private static TrackPlan Answered(TrackPlan plan)
    {
        foreach (var track in plan.Tracks.Where(t => t.Unsure))
        {
            track.Commentary = true;
        }

        return plan;
    }

    private static List<DemuxedFile> Files() =>
    [
        Video("00800.hevc", 3840),
        Video("00800.el.hevc", 1920, dv: true),
        Audio("00800_1100.thd", "MLP FBA", 8, "Dolby TrueHD with Dolby Atmos"),
        Audio("00800_1100.ac3", "AC-3", 6),
        Audio("00800_1101.ac3", "AC-3", 6),
        Audio("00800_1102.dtshd", "DTS", 6, "DTS-HD Master Audio"),
        Audio("00800_1103.ac3", "AC-3", 2),
        Subtitle("00800_1200.sup"),
        Subtitle("00800_1201.sup"),
        Subtitle("00800_1202.sup")
    ];

    [TestMethod]
    public void MapsFilesByPidAndFindsTheLayerAndCores()
    {
        var mapping = MuxBuilder.Map(Film, Files());

        Assert.AreEqual("00800.el.hevc", mapping.EnhancementLayer?.FileName);
        Assert.AreEqual("00800.hevc", mapping.Files[0].FileName);
        Assert.AreEqual("00800_1100.thd", mapping.Files[1].FileName);
        Assert.AreEqual("00800_1101.ac3", mapping.Files[2].FileName);
        Assert.AreEqual("00800_1102.dtshd", mapping.Files[3].FileName);
        Assert.AreEqual("00800_1103.ac3", mapping.Files[4].FileName);
        Assert.AreEqual("00800_1201.sup", mapping.Files[6].FileName);
        Assert.AreEqual("00800_1100.ac3", mapping.Cores.Single().Value.FileName);
        Assert.IsEmpty(mapping.Warnings);
    }

    [TestMethod]
    public void MapsByOrderWithoutPidsAndWarnsOnMissingFiles()
    {
        List<DemuxedFile> files = [Video("a.hevc", 3840), Audio("b1.thd", "MLP FBA", 8), Audio("b2.ac3", "AC-3", 6), Subtitle("c1.sup")];
        var mapping = MuxBuilder.Map(Film, files);

        Assert.AreEqual("b1.thd", mapping.Files[1].FileName);
        Assert.AreEqual("b2.ac3", mapping.Files[2].FileName);
        Assert.HasCount(2, mapping.Warnings);
    }

    [TestMethod]
    public void MapsOnlyTheStreamsDgDemuxWasAskedFor()
    {
        List<DemuxedFile> files = [Video("a.hevc", 3840), Audio("x.dtshd", "DTS", 6, "DTS-HD Master Audio"), Subtitle("y.sup")];
        var pids = new HashSet<int> { Film.Streams[0].Pid, Film.Streams[3].Pid, Film.Streams[6].Pid };
        var mapping = MuxBuilder.Map(Film, files, pids);

        Assert.AreEqual("a.hevc", mapping.Files[0].FileName);
        Assert.AreEqual("x.dtshd", mapping.Files[3].FileName);
        Assert.AreEqual("y.sup", mapping.Files[6].FileName);
        Assert.IsEmpty(mapping.Warnings);
    }

    [TestMethod]
    public void BuildsNamedOutputsInPlanOrder()
    {
        var profile = new RipProfile();
        var plan = Answered(TrackPlanner.Build(Film, profile, "eng"));
        var outputs = MuxBuilder.Outputs(plan, MuxBuilder.Map(Film, Files()), profile, "eng");

        CollectionAssert.AreEqual(new[]
        {
            "2160p - HEVC - HDR10", "English - TrueHD Atmos 7.1", "French - DTS-HD MA 5.1 Dub", "English - AC3 2.0 (Commentary)",
            "English - PGS", "French - PGS"
        }, outputs.Select(o => o.Name).ToArray());
    }

    [TestMethod]
    public void KeepsTheTrueHdCoreWhenAsked()
    {
        var profile = new RipProfile { KeepTrueHdCore = true };
        var plan = TrackPlanner.Build(Film, profile, "eng");
        var outputs = MuxBuilder.Outputs(plan, MuxBuilder.Map(Film, Files()), profile);

        Assert.AreEqual("English - AC3 5.1", outputs[2].Name);
    }

    [TestMethod]
    public void MkvmergeArgumentsCarryFlagsAndOrder()
    {
        var profile = new RipProfile();
        var plan = Answered(TrackPlanner.Build(Film, profile, "eng"));
        var outputs = MuxBuilder.Outputs(plan, MuxBuilder.Map(Film, Files()), profile);
        var arguments = MuxBuilder.MkvmergeArguments("/o/x.mkv", "Film (2020)", outputs, "/w/ch.txt", "/w/tags.xml", (24000, 1001), "/w/bl-el.hevc");
        var text = string.Join(' ', arguments);

        StringAssert.StartsWith(text, "--gui-mode -o /o/x.mkv --title Film (2020) --global-tags /w/tags.xml --chapters /w/ch.txt");
        StringAssert.Contains(text, "--default-duration 0:24000/1001p /w/bl-el.hevc");
        StringAssert.Contains(text, "--track-name 0:English - TrueHD Atmos 7.1 --default-track-flag 0:yes /w/00800_1100.thd");
        StringAssert.Contains(text, "--commentary-flag 0:yes /w/00800_1103.ac3");
        StringAssert.Contains(text, "--forced-display-flag 0:no /w/00800_1200.sup");
        StringAssert.EndsWith(text, "--track-order 0:0,1:0,2:0,3:0,4:0,5:0");
    }

    [TestMethod]
    public void NameValuesUseTheStreams()
    {
        var profile = new RipProfile();
        var plan = TrackPlanner.Build(Film, profile, "eng");
        var outputs = MuxBuilder.Outputs(plan, MuxBuilder.Map(Film, Files()), profile);
        var job = new Job { Title = "Film", Year = 2020, Profile = profile };

        Assert.AreEqual("Film (2020) [2160p][HEVC][HDR10][TrueHD Atmos-7.1][BDRemux]",
            Naming.Apply(profile.FileNameTemplate, MuxBuilder.NameValues(job, outputs)));
    }

    [TestMethod]
    public void ChapterFileIsOgm()
    {
        Assert.AreEqual("CHAPTER01=00:00:00.000\nCHAPTER01NAME=Chapter 01\nCHAPTER02=00:10:00.000\nCHAPTER02NAME=Chapter 02\n" +
                        "CHAPTER03=01:00:00.000\nCHAPTER03NAME=Chapter 03\nCHAPTER04=01:15:00.000\nCHAPTER04NAME=Chapter 04\n",
            MuxBuilder.ChapterFile(Film, ChapterNaming.Numbered));
    }

    [TestMethod]
    public void TagsXmlHasTheIdsAndEscapes()
    {
        var job = new Job { Title = "A & B", Year = 2020, TmdbId = 603, ImdbId = "tt0133093", SourceName = "A.&.B.2020", Playlist = "00800", Edition = "Director's Cut" };
        var xml = MuxBuilder.TagsXml(job, []);

        StringAssert.Contains(xml, "<Name>TMDB</Name><String>movie/603</String>");
        StringAssert.Contains(xml, "<Name>RELEASE_NAME</Name><String>A.&amp;.B.2020</String>");
        StringAssert.Contains(xml, "<Name>EDITION</Name><String>Director&apos;s Cut</String>");
        StringAssert.Contains(xml, "<TargetTypeValue>50</TargetTypeValue>");
    }

    [TestMethod]
    public void TagsXmlWritesYourOwnTags()
    {
        var job = new Job
        {
            Title = "Heat", Year = 1995, SourceName = "HEAT", Playlist = "00800",
            Tags =
            [
                new GlobalTag { Name = "release group", Value = "YANN" },
                new GlobalTag { Name = "COMMENT", Value = "{title} ({year}) from my disc" },
                new GlobalTag { Name = "SOURCE", Value = "UHD-BD" },
                new GlobalTag { Name = "ENCODED_BY", Value = "" }
            ]
        };
        var xml = MuxBuilder.TagsXml(job, []);

        StringAssert.Contains(xml, "<Name>RELEASE_GROUP</Name><String>YANN</String>");
        StringAssert.Contains(xml, "<Name>COMMENT</Name><String>Heat (1995) from my disc</String>");
        StringAssert.Contains(xml, "<Name>SOURCE</Name><String>UHD-BD</String>");
        Assert.HasCount(1, System.Text.RegularExpressions.Regex.Matches(xml, "<Name>SOURCE</Name>"));
        Assert.DoesNotContain("ENCODED_BY", xml);
    }

    [TestMethod]
    public void SmallHelpers()
    {
        Assert.AreEqual(-24, MuxBuilder.Delay("track1 DELAY -24ms.ac3"));
        Assert.IsNull(MuxBuilder.Delay("track1.ac3"));
        Assert.AreEqual(0.45, MuxBuilder.PercentProgress("#GUI#progress 45%")!.Value, 0.0001);
        Assert.IsTrue(MuxBuilder.IsStreamFile("/a/b.sup"));
        Assert.IsFalse(MuxBuilder.IsStreamFile("/a/b.txt"));
        CollectionAssert.AreEqual(new[] { "-i", "/d/BDMV/PLAYLIST/00800.mpls", "-o", "/w/00800" },
            DgDemuxCommand.Expand("-i {input} -o {output}", "/d/BDMV/PLAYLIST/00800.mpls", "/w/00800"));
    }

    [TestMethod]
    public void BuildsDgDemuxArgumentsFromTheProfile()
    {
        const string global = "-i {input} -o {output}";
        var profile = new RipProfile();
        var plan = new TrackPlan
        {
            Tracks =
            [
                new PlannedTrack { Kind = StreamKind.Video, Pid = 0x1011, Keep = true },
                new PlannedTrack { Kind = StreamKind.Video, Pid = 0x1015, Keep = true, DolbyVisionLayer = true },
                new PlannedTrack { Kind = StreamKind.Audio, Pid = 0x1100, Keep = true },
                new PlannedTrack { Kind = StreamKind.Audio, Pid = 0x1101, Keep = false },
                new PlannedTrack { Kind = StreamKind.Subtitle, Pid = 0x12A0, Keep = true }
            ]
        };
        var pids = DgDemuxCommand.DemuxedPids(profile, plan);
        CollectionAssert.AreEqual(new[] { "-i", "/d/00800.mpls", "-o", "/w/00800", "-thd", "2", "-demux", "4113,4117,4352,4768" },
            DgDemuxCommand.Arguments(profile, global, "/d/00800.mpls", "/w/00800", pids!.ToList()));

        profile.KeepTrueHdCore = true;
        profile.DgDemux = new DgDemuxSettings { DemuxOnlyKept = false, Angle = 2, SkipFirstClip = true, StrictPlaylist = true };
        profile.DgDemuxArguments = "-nogaps";
        Assert.IsNull(DgDemuxCommand.DemuxedPids(profile, plan));
        CollectionAssert.AreEqual(new[] { "-i", "/d/x.mpls", "-o", "/w/x", "-ang", "2", "-thd", "1", "-sf", "-strictpl", "-nogaps" },
            DgDemuxCommand.Arguments(profile, global, "/d/x.mpls", "/w/x", null));

        // A full template of its own (0.2.0 profiles) replaces everything.
        profile.DgDemuxArguments = "-i {input} -o {output} -sl";
        CollectionAssert.AreEqual(new[] { "-i", "/d/x.mpls", "-o", "/w/x", "-sl" },
            DgDemuxCommand.Arguments(profile, global, "/d/x.mpls", "/w/x", null));
        Assert.AreEqual(2, profile.Copy().DgDemux.Angle);
    }
}
