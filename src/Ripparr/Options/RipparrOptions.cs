namespace Ripparr.Options;

/// <summary>
/// Settings from appsettings.json, or from environment variables in Docker:
/// <c>Ripparr__WorkFolder=/downloads/ripparr/work</c>, <c>Ripparr__Radarr__ApiKey=...</c>.
/// </summary>
public class RipparrOptions
{
    public const string Section = "Ripparr";

    /// <summary>Folders to look for ISO files and BDMV folders in. Missing folders are skipped.</summary>
    public List<SourceRoot> Sources { get; set; } = [];

    /// <summary>Each job gets its own folder here (job-12) for the unpacked disc and the demuxed streams.</summary>
    public string WorkFolder { get; set; } = "/downloads/ripparr/work";

    /// <summary>Finished files go to DoneFolder/Title (Year)/.</summary>
    public string DoneFolder { get; set; } = "/downloads/ripparr/done";

    /// <summary>
    /// Specarr's address as the browser reaches it (http://10.1.72.11:8185), for "Open in Specarr" on finished
    /// files: tracks, bit rates, Dolby Vision and the source guess. Empty: no links.
    /// </summary>
    public string SpecarrUrl { get; set; } = "";

    /// <summary>Jobs, profiles and settings.</summary>
    public string DatabasePath { get; set; } = "/config/ripparr.db";

    /// <summary>One log file per job (job-12.log).</summary>
    public string LogFolder { get; set; } = "/config/logs";

    /// <summary>
    /// MakeMKV keeps its settings (the key) in $HOME/.MakeMKV. Ripparr runs makemkvcon
    /// with HOME set here, so the key lives in /config and survives a new image.
    /// </summary>
    public string MakeMkvHome { get; set; } = "/config/makemkv";

    /// <summary>Jobs run at the same time. 1 keeps scratch from being read and written by two jobs at once.</summary>
    public int MaxParallelJobs { get; set; } = 1;

    /// <summary>Playlists shorter than this are hidden on the disc page (menus, logos, trailers).</summary>
    public int MinPlaylistSeconds { get; set; } = 120;

    /// <summary>Delete the job folder after a good result. A failed job always keeps it.</summary>
    public bool CleanWorkFolder { get; set; } = true;

    /// <summary>Free space kept on top of what a job needs, in GB.</summary>
    public double SpareSpaceGb { get; set; } = 20;

    /// <summary>
    /// How DGDemux is called. {input} = the playlist (.mpls), {output} = the file
    /// name prefix for the demuxed streams.
    /// </summary>
    public string DgDemuxArguments { get; set; } = "-i {input} -o {output}";

    /// <summary>
    /// Each job keeps its subtitle streams (.sup), the OCR text and the SRT files here for the
    /// subtitle preview: SubtitleFolder/job-12/.
    /// </summary>
    public string SubtitleFolder { get; set; } = "/config/subtitles";

    /// <summary>Pictures of the playlists (to tell the extras apart), a folder per disc and playlist.</summary>
    public string ThumbnailFolder { get; set; } = "/config/thumbnails";

    /// <summary>The subtitle previews of this many recent jobs are kept; older ones are deleted.</summary>
    public int KeepSubtitlePreviews { get; set; } = 30;

    public ToolPaths Tools { get; set; } = new();

    /// <summary>The ISO mount service on the host (host/ripparr_mount.py): jobs read a mounted ISO instead of unpacking it.</summary>
    public IsoMountOptions IsoMount { get; set; } = new();

    public UpdateCheckOptions Updates { get; set; } = new();

    /// <summary>Used to look up the title, year, TMDb and IMDb IDs and the original language.</summary>
    public ArrOptions Radarr { get; set; } = new();

    public ArrOptions Sonarr { get; set; } = new();

    /// <summary>ntfy and Jellyfin when a job ends. The Settings page can change them.</summary>
    public NotificationOptions Notifications { get; set; } = new();

    /// <summary>Copying finished extras into the library's film and series folders. The Settings page can change them.</summary>
    public ExtrasOptions Extras { get; set; } = new();

    public static readonly SourceRoot[] DefaultSources =
    [
        new() { Name = "Scratch", Path = "/downloads" },
        new() { Name = "USB drives", Path = "/usb" }
    ];

    /// <summary>Fills in the defaults for lists the configuration left empty.</summary>
    public RipparrOptions WithDefaults()
    {
        if (Sources.Count == 0)
        {
            Sources = DefaultSources.Select(s => new SourceRoot { Name = s.Name, Path = s.Path }).ToList();
        }

        if (MaxParallelJobs < 1)
        {
            MaxParallelJobs = 1;
        }

        // A tool binary placed in /config/tools wins over the one in the image
        // (DGDemux's site does not let the image build download it).
        var toolsFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath) ?? "/config", "tools");
        Tools.DgDemux = InToolsFolder(toolsFolder, "dgdemux", Tools.DgDemux);
        Tools.MakeMkvCon = InToolsFolder(toolsFolder, "makemkvcon", Tools.MakeMkvCon);

        return this;
    }

    /// <summary>toolsFolder/name when it exists and the setting is still the plain command name.</summary>
    internal static string InToolsFolder(string toolsFolder, string name, string current)
    {
        var candidate = System.IO.Path.Combine(toolsFolder, name);
        return current == name && File.Exists(candidate) ? candidate : current;
    }
}

public class IsoMountOptions
{
    /// <summary>Use the mount service for plain (decrypted) ISO files. Off: always unpack with 7-Zip.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The service's socket, bind-mounted from the host (/run/ripparr-mount:/run/ripparr-mount).</summary>
    public string Socket { get; set; } = "/run/ripparr-mount/mount.sock";

    /// <summary>Where the service mounts the ISOs; left out of the Sources list so a disc doesn't show twice.</summary>
    public string Folder { get; set; } = "/mnt/pc/iso-mounts";

    /// <summary>When the ISO can't be mounted: Ask (the job stops and asks before unpacking) or Unpack (unpack with 7-Zip right away).</summary>
    public MountFallback WhenItFails { get; set; } = MountFallback.Ask;

    /// <summary>
    /// Folders that have another path on the host than in the container (the volume /mnt/usb:/usb):
    /// the service gets host paths, Ripparr reads the mount at its container path. Not needed when
    /// the volumes keep the same path on both sides.
    /// </summary>
    public List<PathMapping> PathMap { get; set; } = [];

    /// <summary>The host's path for a path in the container.</summary>
    public string ToHost(string path) => Map(path, m => m.Container, m => m.Host);

    /// <summary>The container's path for a path on the host.</summary>
    public string ToContainer(string path) => Map(path, m => m.Host, m => m.Container);

    private string Map(string path, Func<PathMapping, string> from, Func<PathMapping, string> to)
    {
        foreach (var mapping in PathMap.OrderByDescending(m => from(m).Length))
        {
            var prefix = from(mapping).TrimEnd('/');
            if (prefix.Length > 0 && (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                return to(mapping).TrimEnd('/') + path[prefix.Length..];
            }
        }

        return path;
    }
}

public class PathMapping
{
    public string Container { get; set; } = "";
    public string Host { get; set; } = "";
}

public enum MountFallback
{
    Ask,
    Unpack
}

public class SourceRoot
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Command names, or full paths when a tool is not on the PATH.</summary>
public class ToolPaths
{
    public string MakeMkvCon { get; set; } = "makemkvcon";
    public string DgDemux { get; set; } = "dgdemux";
    public string MkvMerge { get; set; } = "mkvmerge";
    public string MediaInfo { get; set; } = "mediainfo";
    public string DoviTool { get; set; } = "dovi_tool";
    public string FFprobe { get; set; } = "ffprobe";
    public string FFmpeg { get; set; } = "ffmpeg";
    public string SevenZip { get; set; } = "7z";
    public string Tesseract { get; set; } = "tesseract";
}

public class UpdateCheckOptions
{
    /// <summary>Look for new tool versions on the internet. Nothing is ever installed by the app.</summary>
    public bool Enabled { get; set; } = true;

    public double IntervalHours { get; set; } = 24;

    /// <summary>
    /// MakeMKV 2.x is shown as an update only when this is true: 2.0.0 had problems
    /// opening discs and taking keys, and only decrypting matters here.
    /// </summary>
    public bool AllowMakeMkvMajor { get; set; }
}

public class ArrOptions
{
    /// <summary>For example http://radarr:7878 on the compose network.</summary>
    public string Url { get; set; } = "";

    public string ApiKey { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Where to say that a job ended, and whether Jellyfin rescans its libraries after a good one.</summary>
public class NotificationOptions
{
    /// <summary>The ntfy topic, server included: https://ntfy.sh/my-ripparr or http://ntfy:80/ripparr.</summary>
    public string NtfyUrl { get; set; } = "";

    /// <summary>An access token for a protected topic (tk_...), or empty.</summary>
    public string NtfyToken { get; set; } = "";

    public bool OnDone { get; set; } = true;

    public bool OnFailed { get; set; } = true;

    /// <summary>Dry runs only notify when they fail.</summary>
    public bool DryRuns { get; set; }

    /// <summary>For example http://jellyfin:8096.</summary>
    public string JellyfinUrl { get; set; } = "";

    public string JellyfinApiKey { get; set; } = "";

    /// <summary>Ask Jellyfin to scan its libraries after a good job (not a dry run).</summary>
    public bool RefreshJellyfin { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NtfyConfigured => !string.IsNullOrWhiteSpace(NtfyUrl);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool JellyfinConfigured => !string.IsNullOrWhiteSpace(JellyfinUrl) && !string.IsNullOrWhiteSpace(JellyfinApiKey);
}

/// <summary>
/// Where the library is (as Radarr, Sonarr and Jellyfin see it) and how finished extras get there:
/// Title (Year)/featurettes/ next to the imported film or series.
/// </summary>
public class ExtrasOptions
{
    /// <summary>The movie library root, with its category folders (live-action, anime...).</summary>
    public string MoviesFolder { get; set; } = "/movies";

    /// <summary>The TV library root.</summary>
    public string TvFolder { get; set; } = "/tv";

    /// <summary>Copy each extra on its own once its film or series is in the library. Off: the Extras page's button.</summary>
    public bool AutoCopy { get; set; }

    /// <summary>Read each copy back and compare it with the done file before giving it its name.</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Delete the file in the done folder once it is in the library. Off: it stays until cleaned up.</summary>
    public bool RemoveDoneCopy { get; set; }

    /// <summary>Tell Jellyfin about the new files (the URL and key on the Notifications tab), so they show without a scan.</summary>
    public bool TellJellyfin { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Arrkit.Library.LibraryRoot> Roots => [new(MoviesFolder, false), new(TvFolder, true)];
}
