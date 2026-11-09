using Ripparr.Profiles;

namespace Ripparr.Tests;

[TestClass]
public class NamingTests
{
    private static readonly RipProfile Profile = new();

    private static Dictionary<string, string?> Values(string? edition = null, string? hdr = "DV HDR10") => new()
    {
        ["title"] = "Blade Runner", ["year"] = "1982", ["edition"] = edition, ["resolution"] = "2160p",
        ["videocodec"] = "HEVC", ["hdr"] = hdr, ["audio"] = "TrueHD Atmos-7.1", ["source"] = "BDRemux", ["country"] = null,
        ["releasegroup"] = null
    };

    [TestMethod]
    public void BuildsYannsFileName()
    {
        Assert.AreEqual("Blade Runner (1982) - Final Cut [2160p][HEVC][DV HDR10][TrueHD Atmos-7.1][BDRemux]",
            Naming.Apply(Profile.FileNameTemplate, Values("Final Cut")));
    }

    [TestMethod]
    public void WritesTheYearOnceWhenTheTitleHasIt()
    {
        // Sonarr's title for the 2005 series is "Doctor Who (2005)".
        var values = new Dictionary<string, string?> { ["title"] = "Doctor Who (2005)", ["year"] = "2005", ["episode"] = "S01E11", ["episodetitle"] = null };
        Assert.AreEqual("Doctor Who (2005)", Naming.Apply("{title} ({year})", values));
        Assert.AreEqual("Doctor Who (2005) S01E11", Naming.Apply(Profile.EpisodeTitleTemplate, values), "the year Sonarr needs stays");
        Assert.AreEqual("Doctor Who (2005)", Naming.TitleAndYear("Doctor Who (2005)", 2005));
        Assert.AreEqual("Doctor Who (1963)", Naming.TitleAndYear("Doctor Who", 1963));
        Assert.AreEqual("Doctor Who (2005) (2023)", Naming.TitleAndYear("Doctor Who (2005)", 2023), "another year is kept");
    }

    [TestMethod]
    public void DropsTheEditionGroupAndEmptyBrackets()
    {
        Assert.AreEqual("Blade Runner (1982) [2160p][HEVC][TrueHD Atmos-7.1][BDRemux]",
            Naming.Apply(Profile.FileNameTemplate, Values(hdr: null)));
        Assert.AreEqual("Blade Runner (1982)", Naming.Apply(Profile.TitleTemplate, Values()));
        Assert.AreEqual("Blade Runner (1982) - Final Cut", Naming.Apply(Profile.TitleTemplate, Values("Final Cut")));
    }

    [TestMethod]
    public void LeavesUnknownBracesAlone()
    {
        Assert.AreEqual("Blade Runner {nope}", Naming.Apply("{title} {nope}", Values()));
    }

    [TestMethod]
    public void TrackNameTemplates()
    {
        Assert.AreEqual("English - TrueHD Atmos 7.1", TrackNaming.Audio(Profile, "English", "TrueHD Atmos", "7.1", new TrackFlags()));
    }

    [TestMethod]
    public void SafeFileNames()
    {
        Assert.AreEqual("Mission - Impossible", Naming.SafeFileName("Mission: Impossible"));
        Assert.AreEqual("AC DC Live", Naming.SafeFileName("AC/DC Live"));
        Assert.AreEqual("What", Naming.SafeFileName("What?..."));
    }
}
