namespace Ripparr.Tools;

/// <summary>
/// The tools read file names and titles in the locale's character set. With none set (the image's
/// default is the plain "C" locale) mkvmerge cuts "Red Dwarf S01E06 - Me².mkv" at the "²" and writes
/// a file called "... - Me", so the job cannot find its MKV. Every tool Ripparr starts gets UTF-8.
/// </summary>
public static class Utf8Locale
{
    public const string Name = "C.UTF-8";

    /// <summary>True when the locale variables already say UTF-8 (LC_ALL wins over LC_CTYPE over LANG).</summary>
    public static bool IsUtf8(Func<string, string?> variable)
    {
        var locale = new[] { "LC_ALL", "LC_CTYPE", "LANG" }.Select(variable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        return locale != null && (locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || locale.Contains("utf8", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sets LANG and LC_ALL to C.UTF-8 for this process, so every tool it starts inherits them; a UTF-8 locale already set stays.</summary>
    public static void Ensure()
    {
        if (IsUtf8(Environment.GetEnvironmentVariable))
        {
            return;
        }

        Environment.SetEnvironmentVariable("LANG", Name);
        Environment.SetEnvironmentVariable("LC_ALL", Name);
    }
}
