using Ripparr.Tools;

namespace Ripparr.Tests;

[TestClass]
public class MakeMkvTests
{
    [TestMethod]
    public void ParsesRobotLines()
    {
        var line = MakeMkv.ParseRobot("MSG:1005,0,1,\"MakeMKV v1.18.4 linux(x64-release) started\",\"%1 started\",\"MakeMKV v1.18.4 linux(x64-release)\"");
        Assert.IsNotNull(line);
        Assert.AreEqual("MSG", line.Type);
        Assert.AreEqual(1005, line.Code);
        Assert.AreEqual("MakeMKV v1.18.4 linux(x64-release) started", line.Message);
        Assert.AreEqual("1.18.4", MakeMkv.Version(line.Message!));

        Assert.AreEqual(0.25, MakeMkv.Progress(MakeMkv.ParseRobot("PRGV:100,16384,65536")!)!.Value, 1e-9);
        Assert.IsNull(MakeMkv.ParseRobot("just text"));
        Assert.AreEqual("say \"hi\"", MakeMkv.ParseRobot("MSG:1,0,0,\"say \"\"hi\"\"\"")!.Message);
    }

    [TestMethod]
    public void PicksKeyMessages()
    {
        const string output = """
            MSG:1005,0,1,"MakeMKV v1.18.4 linux(x64-release) started","%1 started","MakeMKV v1.18.4"
            MSG:5021,260,1,"This application version is too old.  Please download the latest version at http://www.makemkv.com/ or enter a registration key to continue using the current version.","x"
            MSG:5010,0,0,"Failed to open disc","Failed to open disc"
            """;
        var messages = MakeMkv.KeyMessages(output);

        Assert.HasCount(1, messages);
        StringAssert.StartsWith(messages[0], "This application version is too old");
    }

    [TestMethod]
    public void WritesTheKeyAndKeepsOtherSettings()
    {
        var home = Path.Combine(Path.GetTempPath(), $"ripparr-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".MakeMKV"));
            File.WriteAllLines(MakeMkv.SettingsPath(home), ["app_DataDir = \"/x\"", "app_Key = \"T-old\""]);
            var key = "T-" + new string('a', 60);

            MakeMkv.WriteKey(home, key);
            Assert.AreEqual(key, MakeMkv.ReadKey(home));
            CollectionAssert.Contains(File.ReadAllLines(MakeMkv.SettingsPath(home)), "app_DataDir = \"/x\"");

            MakeMkv.WriteKey(home, null);
            Assert.IsNull(MakeMkv.ReadKey(home));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [TestMethod]
    public void KeyFormat()
    {
        var key = "T-" + new string('b', 66);
        Assert.IsTrue(MakeMkv.LooksLikeKey(key));
        Assert.IsFalse(MakeMkv.LooksLikeKey("hello"));
        Assert.AreEqual("T-bbb…bbbb", MakeMkv.Mask(key));
        Assert.AreEqual(key, MakeMkv.FindBetaKey($"<code>{key}</code>"));
        Assert.AreEqual("iso:/a/b.iso", MakeMkv.SourceArgument(new Disc.DiscSource("/a/b.iso", Disc.SourceKind.Iso, "/a", 0, DateTime.UtcNow)));
    }
}
