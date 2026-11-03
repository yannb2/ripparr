using Ripparr.Disc;
using Ripparr.Media;

namespace Ripparr.Tests;

[TestClass]
public class StreamFactsTests
{
    private const string Json = """
        {"media":{"track":[
          {"@type":"General","Format":"Matroska"},
          {"@type":"Video","Format":"HEVC","Width":"3840","Height":"2160","FrameRate":"23.976","FrameCount":"172800",
           "HDR_Format":"Dolby Vision / SMPTE ST 2086","HDR_Format_Profile":"dvhe.07.06 / ","HDR_Format_Settings":"BL+EL+RPU / ",
           "HDR_Format_Compatibility":"Blu-ray / HDR10","transfer_characteristics":"PQ"},
          {"@type":"Audio","Format":"MLP FBA","Format_Commercial_IfAny":"Dolby TrueHD with Dolby Atmos","Format_AdditionalFeatures":"16-ch",
           "Channels":"8","ChannelLayout":"L R C LFE Ls Rs Lb Rb","Language":"en"},
          {"@type":"Audio","Format":"DTS","Format_Commercial_IfAny":"DTS-HD Master Audio","Format_AdditionalFeatures":"XLL","Channels":"6","ChannelLayout":"C L R Ls Rs LFE"},
          {"@type":"Audio","Format":"AC-3","Channels":"2","ChannelLayout":"L R"},
          {"@type":"Audio","Format":"E-AC-3","Format_AdditionalFeatures":"JOC","Channels":"6","ChannelLayout":"L R C LFE Ls Rs"},
          {"@type":"Text","Format":"PGS","Language":"fr"}
        ]}}
        """;

    [TestMethod]
    public void NamesAudioLikeRadarr()
    {
        var audio = MediaInfoReader.Parse(Json).Where(f => f.Kind == StreamKind.Audio).ToList();
        CollectionAssert.AreEqual(new[] { "TrueHD Atmos", "DTS-HD MA", "AC3", "EAC3 Atmos" }, audio.Select(a => a.AudioCodec).ToArray());
        CollectionAssert.AreEqual(new[] { "7.1", "5.1", "2.0", "5.1" }, audio.Select(a => a.ChannelWords).ToArray());
    }

    [TestMethod]
    public void ReadsDolbyVisionDualLayer()
    {
        var video = MediaInfoReader.Parse(Json).Single(f => f.Kind == StreamKind.Video);
        Assert.AreEqual("DV HDR10", video.DynamicRange);
        Assert.IsTrue(video.DualLayer);
        Assert.AreEqual("7", video.DolbyVisionProfile);
        Assert.AreEqual("2160p", video.Resolution);
        Assert.AreEqual("HEVC", video.VideoCodec);
        Assert.AreEqual(172800, video.FrameCount);
    }

    [TestMethod]
    public void DynamicRangeWords()
    {
        Assert.AreEqual("HDR10Plus", new StreamFacts { HdrFormat = "SMPTE ST 2094 App 4", HdrCompatibility = "HDR10+ Profile A / HDR10" }.DynamicRange);
        Assert.AreEqual("HDR10", new StreamFacts { HdrFormat = "SMPTE ST 2086", HdrCompatibility = "HDR10" }.DynamicRange);
        Assert.AreEqual("HLG", new StreamFacts { Transfer = "HLG" }.DynamicRange);
        Assert.AreEqual("", new StreamFacts { Transfer = "BT.709" }.DynamicRange);
        Assert.AreEqual("1080p", new StreamFacts { Width = 1920, Height = 800 }.Resolution);
    }

    [TestMethod]
    public void IgnoresBrokenJson()
    {
        Assert.IsEmpty(MediaInfoReader.Parse("not json"));
        Assert.IsEmpty(MediaInfoReader.Parse(null));
    }

    [TestMethod]
    public void ReadsBitRatesSizesAndDetails()
    {
        const string json = """
            {"media":{"track":[
              {"@type":"Video","Format":"HEVC","Width":"3840","Height":"2160","FrameRate":"23.976","BitDepth":"10","ChromaSubsampling":"4:2:0",
               "BitRate":"58000000","StreamSize":"50000000000","Duration":"7200.000",
               "HDR_Format":"Dolby Vision / SMPTE ST 2086","HDR_Format_Profile":"dvhe.07.06 / ","HDR_Format_Settings":"BL+EL+RPU / "},
              {"@type":"Audio","Format":"MLP FBA","Channels":"8","SamplingRate":"48000","BitDepth":"24","BitRate_Mode":"VBR",
               "BitRate_Maximum":"12000000 / 640000","Duration":"7200.000"},
              {"@type":"Text","Format":"PGS","ElementCount":"1534"}
            ]}}
            """;
        var facts = MediaInfoReader.Parse(json);
        Assert.AreEqual(58_000_000, facts[0].BitRate);
        Assert.AreEqual(50_000_000_000, facts[0].StreamSize);
        Assert.AreEqual("3840x2160 · 23.976 fps · 10-bit · 4:2:0 · DV profile 7 (BL+EL)", facts[0].Details);

        // No bit rate from mediainfo: worked out from the file's size and the duration.
        Assert.AreEqual(0, facts[1].BitRate);
        Assert.AreEqual(12_000_000, facts[1].BitRateMaximum);
        Assert.AreEqual(4_000_000, facts[1].BitRateFor(3_600_000_000));
        Assert.AreEqual("48 kHz · 24-bit · 8 ch · VBR", facts[1].Details);

        Assert.AreEqual("PGS · 1534 captions", facts[2].Details);
        Assert.AreEqual(0, facts[2].BitRateFor(1000), "no duration, no bit rate");
    }

    [TestMethod]
    public void PlaylistDetails()
    {
        Assert.AreEqual("2160p · 10-bit · SDR · 23.976 fps", StreamCoding.Details(new PlaylistStream { Kind = StreamKind.Video, Coding = 0x24, Format = 8, Rate = 1 }));
        Assert.AreEqual("96 kHz", StreamCoding.Details(new PlaylistStream { Kind = StreamKind.Audio, Coding = 0x83, Format = 6, Rate = 4 }));
        Assert.AreEqual("", StreamCoding.Details(new PlaylistStream { Kind = StreamKind.Subtitle, Coding = 0x90 }));
    }
}
