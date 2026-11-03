using Ripparr.Tools;

namespace Ripparr.Tests;

[TestClass]
public class ToolCatalogTests
{
    private static ToolDefinition Tool(string id) => ToolCatalog.All.Single(t => t.Id == id);

    [TestMethod]
    public void ReadsInstalledVersions()
    {
        Assert.AreEqual("103.0", ToolCatalog.InstalledVersion(Tool("mkvtoolnix"), "mkvmerge v103.0 ('Asylum') 64-bit"));
        Assert.AreEqual("25.04", ToolCatalog.InstalledVersion(Tool("mediainfo"), "MediaInfo Command line,\nMediaInfoLib - v25.04"));
        Assert.AreEqual("2.3.4", ToolCatalog.InstalledVersion(Tool("dovi_tool"), "dovi_tool 2.3.4"));
        Assert.AreEqual("8.1", ToolCatalog.InstalledVersion(Tool("ffprobe"), "ffprobe version n8.1-12-g1234 Copyright"));
        Assert.AreEqual("23.01", ToolCatalog.InstalledVersion(Tool("7zip"), "7-Zip 23.01 (x64) : Copyright (c) 1999-2023"));
        Assert.AreEqual("25.01", ToolCatalog.InstalledVersion(Tool("7zip"), "7-Zip (z) 25.01 (x64) : Copyright (c) 1999-2025 Igor Pavlov : 2025-08-03"));
        Assert.AreEqual("1.18.4", ToolCatalog.InstalledVersion(Tool("makemkv"), "MSG:1005,0,1,\"MakeMKV v1.18.4 linux(x64-release) started\""));
        Assert.AreEqual("1.0.0.78", ToolCatalog.InstalledVersion(Tool("dgdemux"), "DGDemux 1.0.0.78 by Donald A. Graft"));
    }

    [TestMethod]
    public void FindsTheHighestVersionOnAPage()
    {
        const string page = "<a href='makemkv-oss-1.18.4.tar.gz'>x</a> MakeMKV 2.0.0 for Windows <a>makemkv-bin-1.18.10.tar.gz</a>";
        var pattern = Tool("makemkv").UpstreamPattern!;

        Assert.AreEqual("2.0.0", ToolCatalog.HighestOnPage(pattern, page));
        Assert.AreEqual("1.18.10", ToolCatalog.HighestOnPage(pattern, page, v => ToolCatalog.Major(v) == 1));
        Assert.AreEqual("1.0.0.80", ToolCatalog.HighestOnPage(Tool("dgdemux").UpstreamPattern!,
            "dgdemux_linux_1.0.0.78.zip dgdemux_linux_1.0.0.80.zip dgdemux_1.0.0.90.zip"));
        Assert.AreEqual("104.0", ToolCatalog.HighestOnPage(Tool("mkvtoolnix").UpstreamPattern!,
            "<mkvtoolnix-releases><latest-source><version>104.0</version></latest-source></mkvtoolnix-releases>"));
    }

    [TestMethod]
    public void ComparesVersionsByNumber()
    {
        Assert.IsGreaterThan(0, VersionComparer.Instance.Compare("1.18.10", "1.18.4"));
        Assert.AreEqual(0, VersionComparer.Instance.Compare("103.0", "103"));
        Assert.AreEqual("25.04", ToolCatalog.GitHubTag("{\"tag_name\":\"v25.04\"}"));
    }

    [TestMethod]
    public void UpdateAvailableComparesWithWhatIsInstalled()
    {
        var status = new ToolStatus { Tool = Tool("dovi_tool"), Installed = "2.3.4", Latest = "2.3.5" };
        Assert.IsTrue(status.UpdateAvailable);
        Assert.IsFalse(status with { Latest = "2.3.4" } is { UpdateAvailable: true });
    }
}
