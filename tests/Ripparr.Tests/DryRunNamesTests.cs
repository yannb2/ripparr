using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Media;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public sealed class DryRunNamesTests
{
    private static PlaylistInfo Uhd() => new()
    {
        Name = "00800",
        Mpls = new Mpls
        {
            Version = "0300",
            Items = [],
            Chapters = [],
            Streams =
            [
                new PlaylistStream { Kind = StreamKind.Video, Coding = 0x24, Pid = 0x1011, Format = 8, DynamicRange = 1 },
                new PlaylistStream { Kind = StreamKind.Video, Coding = 0x24, Pid = 0x1015, Format = 6, DynamicRange = 2, DolbyVisionEnhancementLayer = true },
                new PlaylistStream { Kind = StreamKind.Audio, Coding = 0x83, Pid = 0x1100, Language = "eng", Format = 6 },
                new PlaylistStream { Kind = StreamKind.Audio, Coding = 0x81, Pid = 0x1101, Language = "eng", Format = 3 },
                new PlaylistStream { Kind = StreamKind.Subtitle, Coding = 0x90, Pid = 0x1200, Language = "eng" }
            ]
        }
    };

    private static Job Job(RipProfile profile) => new()
    {
        Title = "Film",
        Year = 2020,
        Profile = profile,
        OriginalLanguage = "eng",
        Plan = new TrackPlan
        {
            Tracks =
            [
                new PlannedTrack { StreamIndex = 0, Kind = StreamKind.Video, Codec = "HEVC", Language = "eng", Keep = true },
                new PlannedTrack { StreamIndex = 1, Kind = StreamKind.Video, Codec = "HEVC", Language = "eng", Keep = true, DolbyVisionLayer = true },
                new PlannedTrack { StreamIndex = 2, Kind = StreamKind.Audio, Codec = "TrueHD", Layout = "Multi", Language = "eng", Keep = true, Default = true },
                new PlannedTrack { StreamIndex = 3, Kind = StreamKind.Audio, Codec = "AC3", Layout = "2.0", Language = "eng", Keep = true, Commentary = true },
                new PlannedTrack { StreamIndex = 4, Kind = StreamKind.Subtitle, Codec = "PGS", Language = "eng", Keep = true, HearingImpaired = true }
            ]
        }
    };

    [TestMethod]
    public void WithoutTheQuickReadNamesComeFromThePlaylist()
    {
        var job = Job(new RipProfile());
        var names = DryRunNames.For(job, Uhd());

        CollectionAssert.AreEqual(new[]
        {
            "2160p - HEVC - Dolby Vision Profile 7 HDR10",
            "English - TrueHD",
            "English - AC-3 2.0 (Commentary)",
            "English (SDH) - PGS"
        }, names.Select(n => n.Name).ToArray());
        Assert.IsTrue(names.All(n => n.Guessed));
        var notes = DryRunNames.Notes(job, names).ToList();
        Assert.HasCount(2, notes, "guessed names, and the FEL/MEL that dovi_tool reads");
        StringAssert.Contains(DryRunNames.Line(names[1]), "Audio eng TrueHD default → \"English - TrueHD\" ~");
    }

    [TestMethod]
    public void TheQuickReadsFactsGiveTheRealNamesAndTheCore()
    {
        var job = Job(new RipProfile { KeepTrueHdCore = true });
        job.Plan.Tracks[0].Probed = new StreamFacts
        {
            Kind = StreamKind.Video, Format = "HEVC", Width = 3840, Height = 2160, HdrFormat = "SMPTE ST 2086", HdrCompatibility = "HDR10"
        };
        job.Plan.Tracks[2].Probed = new StreamFacts
        {
            Kind = StreamKind.Audio, Format = "MLP FBA", Commercial = "Dolby TrueHD with Dolby Atmos", Channels = 8,
            ChannelLayout = "L R C LFE Ls Rs Lb Rb"
        };
        var names = DryRunNames.For(job, Uhd());

        Assert.AreEqual("2160p - HEVC - Dolby Vision Profile 7 HDR10", names[0].Name);
        Assert.IsFalse(names[0].Guessed);
        Assert.AreEqual("English - TrueHD Atmos 7.1", names[1].Name);
        Assert.IsFalse(names[1].Guessed);
        Assert.IsTrue(names[2].Core);
        Assert.AreEqual("English - AC-3 5.1", names[2].Name, "the core: at most 5.1");
        StringAssert.Contains(DryRunNames.Line(names[2]), "(TrueHD core)");
    }

    [TestMethod]
    public void DvdAudioChannelsComeFromTheIfo()
    {
        var title = new DvdTitle
        {
            Number = 1, TitleSet = 1, Aspect = "16:9",
            Audio = [new DvdAudio(0, "AC3", 6, 48000, "eng", 0)]
        };
        var playlist = new PlaylistInfo { Name = "title01", Mpls = DvdDisc.ToMpls(title), Dvd = title };
        var job = new Job
        {
            Title = "Show", Profile = new RipProfile(), OriginalLanguage = "eng",
            Plan = TrackPlanner.Build(playlist.Mpls, new RipProfile(), "eng")
        };
        foreach (var track in job.Plan.Tracks)
        {
            track.Keep = true;
        }

        var names = DryRunNames.For(job, playlist).Select(n => n.Name).ToArray();
        Assert.AreEqual("480p - MPEG2", names[0]);
        Assert.AreEqual("English - AC-3 5.1", names[1]);
    }
}
