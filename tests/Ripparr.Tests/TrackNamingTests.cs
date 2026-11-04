using System.Text.Json;
using Ripparr.Jobs;
using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public sealed class TrackNamingTests
{
    private static readonly RipProfile Profile = new();

    [TestMethod]
    public void VideoNamesFollowTheStandard()
    {
        Assert.AreEqual("2160p - HEVC - Dolby Vision Profile 7 FEL HDR10",
            TrackNaming.Video(Profile, "English", "2160p", "HEVC", "DV HDR10", "7", "FEL"));
        Assert.AreEqual("2160p - HEVC - HDR10+", TrackNaming.Video(Profile, "English", "2160p", "HEVC", "HDR10Plus", null, null));
        // SDR is only said at 2160p.
        Assert.AreEqual("2160p - HEVC - SDR", TrackNaming.Video(Profile, "English", "2160p", "HEVC", "", null, null));
        Assert.AreEqual("1080p - AVC", TrackNaming.Video(Profile, "English", "1080p", "AVC", "", null, null));
        Assert.AreEqual("2160p - HEVC - Dolby Vision Profile 5", TrackNaming.Video(Profile, "English", "2160p", "HEVC", "DV", "5", null));
    }

    [TestMethod]
    public void DolbyVisionWordingIsAProfileSetting()
    {
        var profile = new RipProfile { DolbyVisionTemplate = "DV P{dvprofile} {dvlayer}" };
        Assert.AreEqual("2160p - HEVC - DV P7 MEL HDR10", TrackNaming.Video(profile, "English", "2160p", "HEVC", "DV HDR10", "7", "MEL"));
        Assert.AreEqual("8.1", TrackNaming.DolbyVisionProfile("8", "DV HDR10"));
        Assert.AreEqual("8.4", TrackNaming.DolbyVisionProfile("8", "DV HLG"));
        Assert.AreEqual("7", TrackNaming.DolbyVisionProfile("7", "DV HDR10"));
    }

    [TestMethod]
    public void AudioNamesFollowTheStandard()
    {
        Assert.AreEqual("English - TrueHD Atmos 7.1", TrackNaming.Audio(Profile, "English", "TrueHD Atmos", "7.1", new TrackFlags()));
        Assert.AreEqual("French - DTS-HD Master Audio 5.1 Dub", TrackNaming.Audio(Profile, "French", "DTS-HD MA", "5.1", new TrackFlags(Dub: true)));
        Assert.AreEqual("English - AC-3 2.0 (Commentary)", TrackNaming.Audio(Profile, "English", "AC3", "2.0", new TrackFlags(Commentary: true)));
        Assert.AreEqual("English - AC-3 2.0", TrackNaming.Audio(Profile, "English", "AC3", "2.0", new TrackFlags(VisualImpaired: true)));
    }

    [TestMethod]
    public void SubtitleNamesFollowTheStandard()
    {
        Assert.AreEqual("English - PGS", TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags()));
        Assert.AreEqual("English (SDH) - PGS", TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags(HearingImpaired: true)));
        Assert.AreEqual("English (Forced) - PGS", TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags(Forced: true)));
        Assert.AreEqual("English (Commentary) - PGS", TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags(Commentary: true)));
        Assert.AreEqual("English (Commentary) - Director - PGS",
            TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags(Commentary: true), "Director"));
        Assert.AreEqual("French - SRT", TrackNaming.Subtitle(Profile, "French", "SRT", new TrackFlags()));
    }

    [TestMethod]
    public void FirstMatchingFlagWins()
    {
        // SDH comes before Forced.
        Assert.AreEqual("English (SDH) - PGS", TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags(HearingImpaired: true, Forced: true)));
    }

    [TestMethod]
    public void DubIsAudioInAnotherLanguageThanTheFilms()
    {
        var french = new PlannedTrack { Kind = Disc.StreamKind.Audio, Language = "fre" };
        Assert.IsTrue(MuxBuilder.Flags(french, "eng").Dub);
        Assert.IsFalse(MuxBuilder.Flags(french, "fra").Dub);
        Assert.IsFalse(MuxBuilder.Flags(french, null).Dub);
        Assert.IsFalse(MuxBuilder.Flags(new PlannedTrack { Kind = Disc.StreamKind.Audio, Language = "fre", Commentary = true }, "eng").Dub);
        Assert.IsFalse(MuxBuilder.Flags(new PlannedTrack { Kind = Disc.StreamKind.Subtitle, Language = "fre" }, "eng").Dub);
    }

    [TestMethod]
    public void OldProfilesMoveToTheNewNames()
    {
        const string old = """
            {"Id":"uhd","Name":"UHD","VideoNameTemplate":"","AudioNameTemplate":"{language} - {codec} {channels}",
             "CommentaryNameTemplate":"{language} - Commentary - {codec} {channels}","SubtitleNameTemplate":"{language}",
             "ForcedSubtitleNameTemplate":"{language} - Forced","SrtNameTemplate":"{language} - SRT","ForcedSrtNameTemplate":"{language} - Forced - SRT"}
            """;
        var profile = JsonSerializer.Deserialize<RipProfile>(old, JobStore.Json)!;

        Assert.AreEqual(TrackNaming.DefaultVideo, profile.VideoNameTemplate);
        Assert.AreEqual(TrackNaming.DefaultAudio, profile.AudioNameTemplate);
        Assert.AreEqual(TrackNaming.DefaultSubtitle, profile.SubtitleNameTemplate);
        Assert.AreEqual("{language} - {codec} {channels} ({description})", profile.AudioNameOverrides[TrackNaming.Commentary]);
        Assert.AreEqual("{language} (Forced) - {codec}", profile.SubtitleNameOverrides[TrackNaming.Forced]);
        Assert.IsNull(profile.CommentaryNameTemplate);
        Assert.IsNull(profile.SrtNameTemplate);
    }

    [TestMethod]
    public void OldProfilesKeepTheirOwnNames()
    {
        const string old = """
            {"Id":"x","VideoNameTemplate":"{resolution}","AudioNameTemplate":"{language} {codec}",
             "CommentaryNameTemplate":"Commentary ({language})","SubtitleNameTemplate":"{language} sub","ForcedSubtitleNameTemplate":"{language} forced"}
            """;
        var profile = JsonSerializer.Deserialize<RipProfile>(old, JobStore.Json)!;

        Assert.AreEqual("{resolution}", profile.VideoNameTemplate);
        Assert.AreEqual("{language} {codec}", profile.AudioNameTemplate);
        Assert.AreEqual("Commentary ({language})", profile.AudioNameOverrides[TrackNaming.Commentary]);
        Assert.AreEqual("{language} forced", profile.SubtitleNameOverrides[TrackNaming.Forced]);
    }

    [TestMethod]
    public void NewProfilesSurviveARoundTrip()
    {
        var profile = new RipProfile { VideoNameTemplate = "" };
        var copy = JsonSerializer.Deserialize<RipProfile>(JsonSerializer.Serialize(profile, JobStore.Json), JobStore.Json)!;
        Assert.AreEqual("", copy.VideoNameTemplate);
        Assert.HasCount(3, copy.SubtitleNameOverrides);
    }

    [TestMethod]
    public void LanguageSearchFindsNamesAndCodes()
    {
        Assert.AreEqual("fre", Media.Languages.Search("fren")[0].Code);
        Assert.AreEqual("French", Media.Languages.Search("fre")[0].Name);
        Assert.AreEqual("ger", Media.Languages.Search("deu")[0].Code);
        Assert.IsEmpty(Media.Languages.Search(""));
    }

    [TestMethod]
    public void TypedTextIsTheDescriptionNotTheWholeName()
    {
        // The commentary template has {description}: the typed text goes there.
        Assert.AreEqual("English - AC-3 2.0 (Director and Writer)",
            TrackNaming.Audio(Profile, "English", "AC3", "2.0", new TrackFlags(Commentary: true), "Director and Writer"));
        Assert.AreEqual("English (Commentary) - Director - PGS",
            TrackNaming.Subtitle(Profile, "English", "PGS", new TrackFlags(Commentary: true), "Director"));

        // A template without it keeps the text in brackets at the end.
        Assert.AreEqual("English - TrueHD Atmos 7.1 (Home mix)",
            TrackNaming.Audio(Profile, "English", "TrueHD Atmos", "7.1", new TrackFlags(), "Home mix"));
        Assert.AreEqual("1080p - AVC (Open matte)", TrackNaming.Video(Profile, "English", "1080p", "AVC", "", null, null, "Open matte"));

        // "=" types the whole name.
        Assert.AreEqual("My own name", TrackNaming.Audio(Profile, "English", "AC3", "2.0", new TrackFlags(), "= My own name"));

        // Through the job: the plan's typed text.
        var track = new PlannedTrack { Kind = Ripparr.Disc.StreamKind.Audio, Language = "eng", Codec = "AC3", Commentary = true, Name = "Cast" };
        Assert.AreEqual("English - AC-3 (Cast)", MuxBuilder.TrackName(track, new Ripparr.Media.StreamFacts(), Profile), "no stream facts: no channels");
    }
}
