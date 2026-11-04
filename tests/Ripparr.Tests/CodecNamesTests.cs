using Ripparr.Jobs;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class CodecNamesTests
{
    [TestMethod]
    public void CustomNamesReplaceTheLabelWithoutCase()
    {
        var names = new Dictionary<string, string> { ["truehd"] = "Dolby TrueHD", ["AC3"] = " Dolby Digital ", ["DTS"] = "" };

        Assert.AreEqual("Dolby TrueHD", CodecNames.Rename("TrueHD", names));
        Assert.AreEqual("Dolby Digital", CodecNames.Rename("AC3", names));
        Assert.AreEqual("DTS", CodecNames.Rename("DTS", names), "blank keeps the label");
        Assert.AreEqual("FLAC", CodecNames.Rename("FLAC", names));
        Assert.AreEqual("FLAC", CodecNames.Rename("FLAC", null));
    }

    [TestMethod]
    public void ABlankAtmosLabelUsesItsCodecsName()
    {
        var names = new Dictionary<string, string> { ["TrueHD"] = "Dolby TrueHD", ["EAC3"] = "DD+" };

        Assert.AreEqual("Dolby TrueHD Atmos", CodecNames.Rename("TrueHD Atmos", names));
        Assert.AreEqual("DD+ Atmos", CodecNames.Rename("EAC3 Atmos", names));

        names["TrueHD Atmos"] = "Atmos";
        Assert.AreEqual("Atmos", CodecNames.Rename("TrueHD Atmos", names));
    }

    [TestMethod]
    public void TrackNamesUseTheProfilesCodecNames()
    {
        var profile = new RipProfile
        {
            AudioCodecNames = new() { ["TrueHD"] = "Dolby TrueHD" },
            SubtitleCodecNames = new() { ["PGS"] = "PGS Image" },
            VideoCodecNames = new() { ["HEVC"] = "H.265", ["HDR10"] = "HDR", ["FEL"] = "Full Enhancement Layer" }
        };

        Assert.AreEqual("English - Dolby TrueHD Atmos 7.1", TrackNaming.Audio(profile, "English", "TrueHD Atmos", "7.1", new TrackFlags()));
        Assert.AreEqual("English - PGS Image", TrackNaming.Subtitle(profile, "English", "PGS", new TrackFlags()));
        Assert.AreEqual("2160p - H.265 - Dolby Vision Profile 7 Full Enhancement Layer HDR",
            TrackNaming.Video(profile, "English", "2160p", "HEVC", "DV HDR10", "7", "FEL"));
        Assert.AreEqual("2160p - H.265 - HDR", TrackNaming.Video(profile, "English", "2160p", "HEVC", "HDR10", null, null));
        Assert.AreEqual("DV HDR HDR10Plus", CodecNames.RenameWords("DV HDR10 HDR10Plus", new Dictionary<string, string> { ["HDR10"] = "HDR" }),
            "{hdr} keeps Radarr's words unless renamed");
        Assert.AreEqual("DV HDR10 HDR10 Plus", CodecNames.RenameWords("DV HDR10 HDR10Plus", new Dictionary<string, string> { ["HDR10+"] = "HDR10 Plus" }));
    }

    [TestMethod]
    public void WithoutNamesTheBuiltInWordsStay()
    {
        var profile = new RipProfile();

        Assert.AreEqual("English - TrueHD Atmos 7.1", TrackNaming.Audio(profile, "English", "TrueHD Atmos", "7.1", new TrackFlags()));
        Assert.AreEqual("2160p - HEVC - HDR10", TrackNaming.Video(profile, "English", "2160p", "HEVC", "HDR10", null, null));
    }

    [TestMethod]
    public void ACleanSrtOnlyWhereALanguageHasNothingButSdh()
    {
        PlannedTrack Sub(string language, bool sdh, bool forced = false) =>
            new() { Kind = Disc.StreamKind.Subtitle, Language = language, HearingImpaired = sdh, Forced = forced, Codec = "PGS" };
        var englishSdh = Sub("eng", true);
        var englishForced = Sub("eng", false, forced: true);
        var frenchSdh = Sub("fre", true);
        var frenchPlain = Sub("fra", false);
        var germanSdh = Sub("ger", true);
        List<PlannedTrack> tracks = [englishSdh, englishForced, frenchSdh, frenchPlain, germanSdh];

        var picked = SubtitleStep.OnlySdh(tracks, t => t, t => t.HearingImpaired, _ => false).ToList();
        CollectionAssert.AreEqual(new[] { englishSdh, germanSdh }, picked, "French has a plain track; a forced track does not count");

        var withCleanSrt = SubtitleStep.OnlySdh(tracks, t => t, t => t.HearingImpaired, t => t == germanSdh).ToList();
        CollectionAssert.AreEqual(new[] { englishSdh }, withCleanSrt, "German already gets an SRT without SDH");
    }

    [TestMethod]
    public void TheCleanSrtIsNamedAsAPlainTrack()
    {
        var profile = new RipProfile { SrtRemoveSdh = false };
        var track = new PlannedTrack { Kind = Disc.StreamKind.Subtitle, Language = "eng", HearingImpaired = true, Codec = "PGS" };

        Assert.AreEqual("English (SDH) - SRT", SubtitleStep.SrtName(profile, track));
        Assert.AreEqual("English - SRT", SubtitleStep.SrtName(profile, track, sdhRemoved: true));
    }
}
