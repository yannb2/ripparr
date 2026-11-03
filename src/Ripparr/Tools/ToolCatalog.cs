using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ripparr.Options;

namespace Ripparr.Tools;

/// <summary>Where a tool's newest version is read.</summary>
public enum UpstreamKind
{
    None,

    /// <summary>A web page; the highest version the regex finds.</summary>
    Page,

    /// <summary>The latest release of a GitHub repository (owner/name).</summary>
    GitHubRelease
}

/// <summary>A tool in the image: how to ask its version, which version the image pins, where to look for a newer one.</summary>
public sealed record ToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required Func<ToolPaths, string> Command { get; init; }
    public IReadOnlyList<string> VersionArguments { get; init; } = [];
    public required Regex VersionPattern { get; init; }
    public UpstreamKind Upstream { get; init; }

    /// <summary>The page URL(s) or the GitHub owner/name.</summary>
    public IReadOnlyList<string> UpstreamSources { get; init; } = [];

    public Regex? UpstreamPattern { get; init; }
    public string? ReleaseNotes { get; init; }

    /// <summary>The version the Dockerfile installed (RIPPARR_PIN_MAKEMKV...).</summary>
    public string? Pinned => Environment.GetEnvironmentVariable($"RIPPARR_PIN_{Id.ToUpperInvariant()}");
}

public static partial class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new()
        {
            Id = "makemkv", Name = "MakeMKV", Purpose = "Decrypts an ISO, a BDMV folder or a disc into a plain backup folder.",
            Command = t => t.MakeMkvCon,
            VersionArguments = ["-r", "--cache=1", "info", "disc:9999"],
            VersionPattern = MakeMkvVersion(),
            Upstream = UpstreamKind.Page,
            UpstreamSources = ["https://www.makemkv.com/download/"],
            UpstreamPattern = MakeMkvUpstream(),
            ReleaseNotes = "https://www.makemkv.com/download/history.html"
        },
        new()
        {
            Id = "dgdemux", Name = "DGDemux", Purpose = "Splits a playlist into its streams, Dolby Vision layer included.",
            Command = t => t.DgDemux,
            VersionPattern = FourPartVersion(),
            Upstream = UpstreamKind.Page,
            UpstreamSources = ["https://www.rationalqm.us/dgdemux/binaries/", "https://www.rationalqm.us/dgdemux/dgdemux.html"],
            UpstreamPattern = DgDemuxUpstream(),
            ReleaseNotes = "https://www.rationalqm.us/board/viewforum.php?f=15"
        },
        new()
        {
            Id = "mkvtoolnix", Name = "MKVToolNix", Purpose = "Builds the MKV (mkvmerge) and reads it back for the checks.",
            Command = t => t.MkvMerge, VersionArguments = ["--version"],
            VersionPattern = VPrefixedVersion(),
            Upstream = UpstreamKind.Page,
            UpstreamSources = ["https://mkvtoolnix.download/latest-release.xml"],
            UpstreamPattern = XmlVersion(),
            ReleaseNotes = "https://mkvtoolnix.download/doc/NEWS.md"
        },
        new()
        {
            Id = "mediainfo", Name = "MediaInfo", Purpose = "Reads each stream: codec, channels, HDR format.",
            Command = t => t.MediaInfo, VersionArguments = ["--version"],
            VersionPattern = VPrefixedVersion(),
            Upstream = UpstreamKind.GitHubRelease,
            UpstreamSources = ["MediaArea/MediaInfo"],
            ReleaseNotes = "https://mediaarea.net/MediaInfo/ChangeLog"
        },
        new()
        {
            Id = "dovi_tool", Name = "dovi_tool", Purpose = "Merges the Dolby Vision layer and checks the RPU.",
            Command = t => t.DoviTool, VersionArguments = ["--version"],
            VersionPattern = ThreePartVersion(),
            Upstream = UpstreamKind.GitHubRelease,
            UpstreamSources = ["quietvoid/dovi_tool"],
            ReleaseNotes = "https://github.com/quietvoid/dovi_tool/releases"
        },
        new()
        {
            Id = "ffprobe", Name = "FFmpeg", Purpose = "ffprobe reads the streams of a file, ffmpeg takes the playlist pictures (BtbN static build).",
            Command = t => t.FFprobe, VersionArguments = ["-version"],
            VersionPattern = FfprobeVersion(),
            // BtbN keeps one "latest" release; its asset names hold the newest release branch (n9.0).
            Upstream = UpstreamKind.Page,
            UpstreamSources = ["https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest"],
            UpstreamPattern = BtbNBranch(),
            ReleaseNotes = "https://ffmpeg.org/download.html#releases"
        },
        new()
        {
            Id = "tesseract", Name = "Tesseract", Purpose = "Reads picture subtitles (OCR) for the SRT tracks.",
            Command = t => t.Tesseract, VersionArguments = ["--version"],
            VersionPattern = TesseractVersion()
            // From Ubuntu's packages: it moves with the base image, not with a pin.
        },
        new()
        {
            Id = "7zip", Name = "7-Zip", Purpose = "Reads and unpacks ISO files (UDF), no mount needed.",
            Command = t => t.SevenZip,
            VersionPattern = SevenZipVersion(),
            Upstream = UpstreamKind.GitHubRelease,
            UpstreamSources = ["ip7z/7zip"],
            ReleaseNotes = "https://www.7-zip.org/history.txt"
        }
    ];

    /// <summary>The installed version in a tool's own output.</summary>
    public static string? InstalledVersion(ToolDefinition tool, string output) =>
        tool.VersionPattern.Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The highest version the upstream pattern finds on a page.</summary>
    public static string? HighestOnPage(Regex pattern, string page, Func<string, bool>? accept = null)
    {
        return pattern.Matches(page)
            .Select(m => m.Groups[1].Value)
            .Where(v => accept?.Invoke(v) ?? true)
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>
    /// The highest plain version tag (1.2.3 or v1.2.3) of a GitHub releases answer, a list or a single
    /// release. Other tags are skipped: dovi_tool's repository also has libdovi-3.x releases.
    /// </summary>
    public static string? GitHubTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var releases = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];
            return releases
                .Where(r => r.ValueKind == JsonValueKind.Object
                            && !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
                            && !(r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True))
                .Select(r => r.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "")
                .Where(t => PlainVersionTag().IsMatch(t))
                .Select(t => t.TrimStart('v', 'V'))
                .OrderByDescending(v => v, VersionComparer.Instance)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int Major(string version) =>
        int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;

    [GeneratedRegex(@"MakeMKV v(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex MakeMkvVersion();

    // The download page names each build: "MakeMKV 1.18.4 for Windows", or the tarballs makemkv-oss-1.18.4.tar.gz.
    [GeneratedRegex(@"(?:MakeMKV v?|makemkv-(?:oss|bin)-)(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MakeMkvUpstream();

    [GeneratedRegex(@"(\d+\.\d+\.\d+\.\d+)")]
    private static partial Regex FourPartVersion();

    [GeneratedRegex(@"dgdemux_linux_(\d+\.\d+\.\d+\.\d+)\.zip", RegexOptions.IgnoreCase)]
    private static partial Regex DgDemuxUpstream();

    [GeneratedRegex(@"\bv(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VPrefixedVersion();

    [GeneratedRegex(@"<version>\s*(\d+\.\d+(?:\.\d+)?)\s*</version>")]
    private static partial Regex XmlVersion();

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex ThreePartVersion();

    [GeneratedRegex(@"^[vV]?\d+(?:\.\d+)+$")]
    private static partial Regex PlainVersionTag();

    [GeneratedRegex(@"ffmpeg-n(\d+\.\d+)-latest-linux64-gpl-\d")]
    private static partial Regex BtbNBranch();

    [GeneratedRegex(@"version n?(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex FfprobeVersion();

    [GeneratedRegex(@"tesseract v?(\d+\.\d+\.\d+)")]
    private static partial Regex TesseractVersion();

    [GeneratedRegex(@"7-Zip (?:\([az]\) )?(?:\[\d+\] )?(\d+\.\d+)")]
    private static partial Regex SevenZipVersion();
}

/// <summary>Compares "1.18.4" and "1.18.10" by their numbers, not as text.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static int[] Parts(string? version) =>
        (version ?? "").Split('.', '-', '+')
        .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .ToArray();
}
