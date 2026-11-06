using Ripparr.Tools;

namespace Ripparr.Tests;

[TestClass]
public class Utf8LocaleTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [TestMethod]
    public void NoLocaleOrThePlainCLocaleIsNotUtf8()
    {
        Assert.IsFalse(Utf8Locale.IsUtf8(Env()));
        Assert.IsFalse(Utf8Locale.IsUtf8(Env(("LANG", "C"))));
        Assert.IsFalse(Utf8Locale.IsUtf8(Env(("LANG", "en_US.UTF-8"), ("LC_ALL", "POSIX"))));
    }

    [TestMethod]
    public void AUtf8LocaleIsKept()
    {
        Assert.IsTrue(Utf8Locale.IsUtf8(Env(("LANG", "C.UTF-8"))));
        Assert.IsTrue(Utf8Locale.IsUtf8(Env(("LC_ALL", "fr_CA.utf8"))));
        Assert.IsTrue(Utf8Locale.IsUtf8(Env(("LANG", "C"), ("LC_CTYPE", "en_GB.UTF-8"))));
    }

    [TestMethod]
    public async Task ToolsStartedAfterwardsGetUtf8()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Utf8Locale.Ensure();
        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "printf '%s|%s' \"$LANG\" \"$LC_ALL\""], TimeSpan.FromSeconds(10));

        Assert.IsTrue(Utf8Locale.IsUtf8(key => key switch
        {
            "LC_ALL" => result.Output.Split('|')[1],
            "LANG" => result.Output.Split('|')[0],
            _ => null
        }), result.Output);
    }
}
