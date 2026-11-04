using Ripparr.Jobs;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class CodecNamesTests
{
    [TestMethod]
    public void AudioLabelsAreMuxarrs()
    {
        CollectionAssert.AreEqual(new[] { "AC-3", "E-AC-3 Atmos", "DTS-HD Master Audio", "DTS:X", "TrueHD Atmos" },
            new[] { "AC3", "EAC3 Atmos", "DTS-HD MA", "DTS-X", "TrueHD Atmos" }.Select(CodecNames.Label).ToArray());

        // Yann's Muxarr names, copied as they are.
        var names = new Dictionary<string, string> { ["E-AC-3"] = "Dolby E-AC-3", ["TrueHD Atmos"] = "Dolby TrueHD/Atmos" };
        Assert.AreEqual("Dolby E-AC-3", CodecNames.RenameAudio("EAC3", names));
        Assert.AreEqual("Dolby E-AC-3 Atmos", CodecNames.RenameAudio("EAC3 Atmos", names), "blank Atmos label: codec name + Atmos");
        Assert.AreEqual("Dolby TrueHD/Atmos", CodecNames.RenameAudio("TrueHD Atmos", names));
        Assert.AreEqual("DTS-HD Master Audio", CodecNames.RenameAudio("DTS-HD MA", names), "nothing set: Muxarr's label");
    }

    [TestMethod]
    public void SavedNamesMoveToTheMuxarrLabels()
    {
        var names = new Dictionary<string, string> { ["AC3"] = "Dolby Digital", ["DTS-HD MA"] = "", ["EAC3"] = "old", ["E-AC-3"] = "Dolby E-AC-3" };

        CodecNames.MoveToMuxarrLabels(names);

        Assert.AreEqual("Dolby Digital", names["AC-3"]);
        Assert.AreEqual("Dolby E-AC-3", names["E-AC-3"], "a name already under the new label wins");
        Assert.IsFalse(names.ContainsKey("AC3") || names.ContainsKey("EAC3") || names.ContainsKey("DTS-HD MA"));
    }

    [TestMethod]
    public void CommentaryIsNotWrittenTwice()
    {
        var profile = new RipProfile();
        profile.AudioNameOverrides["commentary"] = "{language} - {codec} {channels} - Commentary {description}";

        Assert.AreEqual("English - AC-3 2.0 - Commentary", TrackNaming.Audio(profile, "English", "AC3", "2.0", new TrackFlags(Commentary: true)));
        Assert.AreEqual("English - AC-3 2.0 - Commentary Director", TrackNaming.Audio(profile, "English", "AC3", "2.0", new TrackFlags(Commentary: true), "Director"));
    }

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

[TestClass]
public class DescriptionTokenTests
{
    private static readonly TrackFlags Commentary = new(Commentary: true);

    [TestMethod]
    public void SubtitleCommentaryTakesTheDescriptionBox()
    {
        var profile = new RipProfile();

        Assert.AreEqual("English (Commentary) - PGS", TrackNaming.Subtitle(profile, "English", "PGS", Commentary));
        Assert.AreEqual("English (Commentary) - Director - PGS", TrackNaming.Subtitle(profile, "English", "PGS", Commentary, "Director"));
        Assert.AreEqual("English - AC-3 2.0 (Commentary)", TrackNaming.Audio(profile, "English", "AC3", "2.0", Commentary));
        Assert.AreEqual("English - AC-3 2.0 (Director)", TrackNaming.Audio(profile, "English", "AC3", "2.0", Commentary, "Director"));
    }

    [TestMethod]
    public void SavedProfilesMoveFromTrackNameToDescription()
    {
        const string json = """
            {"Name":"Old","VideoNameTemplate":"{resolution} - {trackname}","AudioNameTemplate":"{language} - {codec}",
             "SubtitleNameTemplate":"{language} {dub} - {codec}",
             "SubtitleNameOverrides":{"commentary":"{language} (Commentary) - {trackname} - {codec}","forced":"{language} (Forced) - {codec}"}}
            """;
        var profile = System.Text.Json.JsonSerializer.Deserialize<RipProfile>(json)!;

        Assert.AreEqual("{resolution} - {description}", profile.VideoNameTemplate);
        Assert.AreEqual("{language} (Commentary) - {description} - {codec}", profile.SubtitleNameOverrides["commentary"]);
        Assert.AreEqual("{language} (Forced) - {codec}", profile.SubtitleNameOverrides["forced"]);
        Assert.AreEqual("{language} (Commentary) - {description} - {codec}", new RipProfile().SubtitleNameOverrides["commentary"]);
    }
}
