using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ripparr.Media;
using Ripparr.Options;

namespace Ripparr.Disc;

/// <summary>Reads a source's playlists, clip sizes and encryption. Results are kept in memory per path and date.</summary>
public sealed partial class DiscAnalyzer(IOptions<RipparrOptions> options, ILogger<DiscAnalyzer> logger)
{
    private readonly RipparrOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, (DateTime Modified, DiscAnalysis Analysis)> _cache = new();

    public TimeSpan Minimum => TimeSpan.FromSeconds(_options.MinPlaylistSeconds);

    /// <summary>Finds a source by its path, if it is under one of the roots.</summary>
    public DiscSource? Describe(string path)
    {
        var full = Path.GetFullPath(path);
        var root = _options.Sources.FirstOrDefault(r => IsUnder(full, r.Path));
        var rootPath = root?.Path ?? Path.GetDirectoryName(full) ?? "/";
        if (File.Exists(full) && full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            var info = new FileInfo(full);
            return new DiscSource(full, SourceKind.Iso, rootPath, info.Length, info.LastWriteTimeUtc);
        }

        if (Directory.Exists(full) && SourceFinder.DiscParent(full) is { } parent)
        {
            return SourceFinder.Find([new SourceRoot { Path = parent }], [])
                .Select(s => s with { Root = rootPath })
                .FirstOrDefault();
        }

        return null;
    }

    public async Task<DiscAnalysis> AnalyseAsync(DiscSource source, CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(source.Path, out var cached) && cached.Modified == source.Modified)
        {
            return cached.Analysis;
        }

        var analysis = new DiscAnalysis { Source = source };
        try
        {
            if (source.Kind == SourceKind.Iso)
            {
                await ReadIsoAsync(analysis, cancellationToken);
            }
            else if (source.Dvd)
            {
                ReadDvd(analysis, SourceFinder.DirIn(source.Path, "VIDEO_TS")!);
            }
            else
            {
                ReadFolder(analysis);
            }

            analysis.Classify(Minimum);
            if (source.Kind == SourceKind.Folder && analysis.MainFeature is { } main && main.Mpls.Clips.Count > 0)
            {
                var clip = ClipPath(source.Path, main.Mpls.Clips[0]);
                analysis.Encryption = clip == null ? Encryption.Unknown : EncryptionCheck.Check(clip);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not read {Path}", source.Path);
            analysis.Error = e.Message;
        }

        if (analysis.Playlists.Count == 0 && analysis.Error == null)
        {
            analysis.Error = analysis.IsDvd ? "No titles found in VIDEO_TS/VIDEO_TS.IFO." : "No playlists found in BDMV/PLAYLIST.";
        }

        _cache[source.Path] = (source.Modified, analysis);
        return analysis;
    }

    /// <summary>The .m2ts file of a clip in a folder source (BDMV/STREAM/00055.m2ts).</summary>
    public static string? ClipPath(string folder, string clip)
    {
        var bdmv = SourceFinder.DirIn(folder, "BDMV");
        var stream = bdmv == null ? null : SourceFinder.DirIn(bdmv, "STREAM");
        return stream == null ? null : SourceFinder.FileIn(stream, clip + ".m2ts");
    }

    /// <summary>True when the folder has what the job needs: the playlist, or the clip of a made one.</summary>
    public static bool HasPlaylist(string folder, PlaylistInfo playlist) => playlist.FromClip != null
        ? ClipPath(folder, playlist.FromClip) != null
        : PlaylistPath(folder, playlist.Name) != null;

    /// <summary>The .mpls file of a playlist in a folder source.</summary>
    public static string? PlaylistPath(string folder, string playlist)
    {
        var bdmv = SourceFinder.DirIn(folder, "BDMV");
        var playlists = bdmv == null ? null : SourceFinder.DirIn(bdmv, "PLAYLIST");
        return playlists == null ? null : SourceFinder.FileIn(playlists, playlist + ".mpls");
    }

    private void ReadFolder(DiscAnalysis analysis)
    {
        var folder = analysis.Source.Path;
        var bdmv = SourceFinder.DirIn(folder, "BDMV") ?? throw new InvalidDataException("No BDMV folder.");
        analysis.HasAacsFolder = SourceFinder.DirIn(folder, "AACS") != null;
        ReadPlaylists(analysis, SourceFinder.DirIn(bdmv, "PLAYLIST"));

        var stream = SourceFinder.DirIn(bdmv, "STREAM");
        if (stream != null)
        {
            foreach (var file in new DirectoryInfo(stream).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (file.Extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.ClipSizes[Path.GetFileNameWithoutExtension(file.Name)] = file.Length;
                }
            }
        }

        AddClipPlaylists(analysis, SourceFinder.DirIn(bdmv, "CLIPINF"));
        analysis.DiscTitle = ReadDiscTitle(bdmv);
    }

    /// <summary>Clips this big that no playlist plays get a playlist of their own.</summary>
    public const long ClipPlaylistMinimumBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Some discs have no playlist for the film: their BD-J menu builds it at play time (The
    /// Fabelmans UHD: 4 short playlists, the film one 82 GB clip none of them plays). Each big clip
    /// no playlist plays gets a playlist made from its CLIPINF file, listed as "Clip 00294".
    /// </summary>
    internal static void AddClipPlaylists(DiscAnalysis analysis, string? clipInfo)
    {
        if (clipInfo == null)
        {
            return;
        }

        var played = analysis.Playlists
            .SelectMany(p => p.Mpls.Clips.Concat(p.Mpls.SubPaths.SelectMany(s => s.Clips)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (clip, size) in analysis.ClipSizes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (size < ClipPlaylistMinimumBytes || played.Contains(clip) || SourceFinder.FileIn(clipInfo, clip + ".clpi") is not { } file)
            {
                continue;
            }

            try
            {
                var bytes = Clpi.Read(file).ToPlaylist(clip).Build();
                analysis.Playlists.Add(new PlaylistInfo
                {
                    Name = PlaylistInfo.ClipPrefix + clip, Mpls = Mpls.Parse(bytes), FromClip = clip, MadePlaylist = bytes
                });
            }
            catch (InvalidDataException e)
            {
                analysis.Notes.Add($"{clip}.m2ts ({Format.Size(size)}) is not in any playlist, and its {clip}.clpi could not be read: {e.Message}");
            }
        }
    }

    /// <summary>
    /// A DVD: its titles from the IFO files. CSS (if any) only scrambles the VOBs, and MakeMKV
    /// rips every DVD job, so the encryption is not looked at.
    /// </summary>
    internal static void ReadDvd(DiscAnalysis analysis, string videoTs)
    {
        analysis.IsDvd = true;
        analysis.Encryption = Encryption.Unknown;
        analysis.Playlists.AddRange(DvdDisc.Playlists(DvdIfo.Read(videoTs), analysis.ClipSizes));
    }

    private async Task ReadIsoAsync(DiscAnalysis analysis, CancellationToken cancellationToken)
    {
        var reader = new IsoReader(_options.Tools.SevenZip);
        var (entries, error) = await reader.ListAsync(analysis.Source.Path, cancellationToken);
        if (entries == null)
        {
            analysis.Error = error;
            return;
        }

        var dvd = entries.Any(e => e.Path.StartsWith("VIDEO_TS/", StringComparison.OrdinalIgnoreCase)
                                   && e.Path.EndsWith(".IFO", StringComparison.OrdinalIgnoreCase))
                  && !entries.Any(e => e.Path.StartsWith("BDMV/", StringComparison.OrdinalIgnoreCase));
        if (dvd)
        {
            await ReadDvdIsoAsync(analysis, reader, cancellationToken);
            return;
        }

        analysis.HasAacsFolder = entries.Any(e => e.Path.StartsWith("AACS", StringComparison.OrdinalIgnoreCase));
        analysis.Encryption = analysis.HasAacsFolder ? Encryption.Likely : Encryption.None;
        foreach (var entry in entries.Where(e => !e.Folder))
        {
            var parts = entry.Path.Split('/');
            if (parts.Length == 3 && parts[0].Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                                  && parts[1].Equals("STREAM", StringComparison.OrdinalIgnoreCase)
                                  && parts[2].EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase))
            {
                analysis.ClipSizes[Path.GetFileNameWithoutExtension(parts[2])] = entry.Size;
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}");
        try
        {
            var result = await reader.ExtractAsync(analysis.Source.Path, temp,
                ["BDMV/PLAYLIST/*", "BDMV/CLIPINF/*", "BDMV/META/DL/*"], cancellationToken);
            if (!result.Success)
            {
                analysis.Error = $"7-Zip could not unpack the playlists: {result.LastLine}";
                return;
            }

            var bdmv = SourceFinder.DirIn(temp, "BDMV");
            ReadPlaylists(analysis, bdmv == null ? null : SourceFinder.DirIn(bdmv, "PLAYLIST"));
            AddClipPlaylists(analysis, bdmv == null ? null : SourceFinder.DirIn(bdmv, "CLIPINF"));
            analysis.DiscTitle = bdmv == null ? null : ReadDiscTitle(bdmv);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task ReadDvdIsoAsync(DiscAnalysis analysis, IsoReader reader, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}");
        try
        {
            var result = await reader.ExtractAsync(analysis.Source.Path, temp, ["VIDEO_TS/*.IFO", "VIDEO_TS/*.BUP"], cancellationToken);
            var videoTs = SourceFinder.DirIn(temp, "VIDEO_TS");
            if (!result.Success || videoTs == null)
            {
                analysis.IsDvd = true;
                analysis.Error = $"7-Zip could not unpack the DVD's IFO files: {result.LastLine}";
                return;
            }

            ReadDvd(analysis, videoTs);
            analysis.DiscTitle = IsoVolumeLabel(analysis.Source.Path);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// An ISO's volume label (the primary volume descriptor at sector 16): a DVD's own name, such
    /// as DOCTOR_WHO_S1_D1. Null when blank or not an ISO 9660 image.
    /// </summary>
    internal static string? IsoVolumeLabel(string iso)
    {
        try
        {
            using var stream = File.OpenRead(iso);
            var sector = new byte[2048];
            stream.Seek(16 * 2048, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sector.Length) < 72 || sector[0] != 1 || System.Text.Encoding.ASCII.GetString(sector, 1, 5) != "CD001")
            {
                return null;
            }

            var label = System.Text.Encoding.ASCII.GetString(sector, 40, 32).Trim(' ', '\0');
            return label.Length == 0 ? null : label;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ReadPlaylists(DiscAnalysis analysis, string? folder)
    {
        if (folder == null)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                     .Where(f => f.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                analysis.Playlists.Add(new PlaylistInfo { Name = Path.GetFileNameWithoutExtension(file), Mpls = Mpls.Read(file) });
            }
            catch (InvalidDataException e)
            {
                analysis.Notes.Add($"{Path.GetFileName(file)} could not be read: {e.Message}");
            }
        }
    }

    /// <summary>The disc's own name from BDMV/META/DL/bdmt_eng.xml (di:name).</summary>
    internal static string? ReadDiscTitle(string bdmv)
    {
        var meta = SourceFinder.DirIn(bdmv, "META");
        var dl = meta == null ? null : SourceFinder.DirIn(meta, "DL");
        if (dl == null)
        {
            return null;
        }

        var file = SourceFinder.FileIn(dl, "bdmt_eng.xml")
                   ?? Directory.EnumerateFiles(dl, "bdmt_*.xml").FirstOrDefault();
        if (file == null)
        {
            return null;
        }

        var match = DiscNameRegex().Match(File.ReadAllText(file));
        if (!match.Success)
        {
            return null;
        }

        var name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return name.Length == 0 || name.Equals("Blu-ray", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>Whether a path is inside one of the configured source folders.</summary>
    public bool InSources(string path) => _options.Sources.Any(r => IsUnder(Path.GetFullPath(path), r.Path));

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(root).TrimEnd('/') + "/";
        return path.StartsWith(full, StringComparison.Ordinal) || path == full.TrimEnd('/');
    }

    [GeneratedRegex(@"<di:name>([^<]*)</di:name>", RegexOptions.IgnoreCase)]
    private static partial Regex DiscNameRegex();
}
