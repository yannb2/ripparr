using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ripparr.Jobs;
using Ripparr.Options;

namespace Ripparr.Disc;

/// <summary>
/// Three pictures of a playlist (at a tenth, half and nine tenths of it), to tell the extras on a
/// disc apart: they have no names. Taken with ffmpeg once and kept in ThumbnailFolder.
/// </summary>
public sealed class Thumbnails(IOptions<RipparrOptions> options, JobStore store, ILogger<Thumbnails> logger)
{
    public const int Count = 3;
    public static readonly double[] Positions = [0.1, 0.5, 0.9];

    /// <summary>Clips bigger than this are not streamed out of an ISO just for pictures.</summary>
    public const long MaxIsoClip = 8L * 1024 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _work = new();

    public static string Key(DiscSource source, string playlist)
    {
        var text = $"{source.Path}|{source.Modified.Ticks}|{playlist}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    public string PicturePath(string key, int index) =>
        Path.Combine(options.Value.ThumbnailFolder, key, $"{index.ToString(CultureInfo.InvariantCulture)}.jpg");

    /// <summary>The picture's file, made with the two others on the first ask; or why there is none.</summary>
    public async Task<(string? File, string? Error)> GetAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, int index)
    {
        var key = Key(source, playlist.Name);
        var file = PicturePath(key, index);
        if (File.Exists(file))
        {
            return (file, null);
        }

        var work = _work.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => MakeAsync(source, analysis, playlist, key)));
        string? error;
        try
        {
            error = await work.Value;
        }
        finally
        {
            _work.TryRemove(key, out _);
        }

        return File.Exists(file) ? (file, null) : (null, error ?? "No picture at this point.");
    }

    private async Task<string?> MakeAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, string key)
    {
        var folder = Path.Combine(options.Value.ThumbnailFolder, key);
        Directory.CreateDirectory(folder);
        try
        {
            return source.Kind == SourceKind.Iso
                ? await FromIsoAsync(source, analysis, playlist, folder)
                : await FromFolderAsync(source, analysis, playlist, folder);
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(e, "Pictures of playlist {Playlist} failed", playlist.Name);
            return e.Message;
        }
    }

    /// <summary>
    /// Where a point of the playlist falls: which clip, and how far into the clip's file. A playlist
    /// can play only part of a clip (TV discs cut each episode out of one big clip): the item's in
    /// time, counted from the earliest in time any playlist uses for that clip (<paramref name="clipStarts"/>),
    /// is added.
    /// </summary>
    public static (string Clip, double Seconds) Locate(Mpls playlist, double fraction, IReadOnlyDictionary<string, uint>? clipStarts = null)
    {
        var target = playlist.Duration.TotalSeconds * fraction;
        var start = 0.0;
        foreach (var item in playlist.Items)
        {
            var length = item.Duration45k / (double)Mpls.ClockRate;
            if (target < start + length || item == playlist.Items[^1])
            {
                return (item.Clip, Offset(item, clipStarts) + Math.Clamp(target - start, 0, Math.Max(0, length - 1)));
            }

            start += length;
        }

        return (playlist.Items[0].Clip, 0);
    }

    private static double Offset(PlayItem item, IReadOnlyDictionary<string, uint>? clipStarts) =>
        clipStarts != null && clipStarts.TryGetValue(item.Clip, out var first) && item.InTime > first
            ? (item.InTime - first) / (double)Mpls.ClockRate
            : 0;

    /// <summary>The earliest in time each clip is played from, over all the disc's playlists.</summary>
    public static Dictionary<string, uint> ClipStarts(DiscAnalysis analysis) =>
        analysis.Playlists.SelectMany(p => p.Mpls.Items)
            .GroupBy(i => i.Clip, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(i => i.InTime), StringComparer.OrdinalIgnoreCase);

    /// <summary>A folder: ffmpeg seeks straight to each point.</summary>
    private async Task<string?> FromFolderAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, string folder)
    {
        var disc = SourceFinder.BdmvParent(source.Path) ?? source.Path;
        var starts = ClipStarts(analysis);
        string? error = null;
        for (var i = 0; i < Count; i++)
        {
            var (clip, seconds) = Locate(playlist.Mpls, Positions[i], starts);
            var path = DiscAnalyzer.ClipPath(disc, clip);
            if (path == null)
            {
                error = $"Clip {clip}.m2ts is missing.";
                continue;
            }

            error = await RunFfmpegAsync(
                ["-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1"],
                Path.Combine(folder, $"{i}.jpg"), stdin: null) ?? error;
        }

        return error;
    }

    /// <summary>
    /// An ISO: 7-Zip streams the playlist's first clip into ffmpeg, which looks at key frames only
    /// and keeps the first one past each point. No seeking in a pipe, so only clips of a sensible size.
    /// </summary>
    private async Task<string?> FromIsoAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, string folder)
    {
        if (await FromMountedIsoAsync(source, analysis, playlist, folder) is { } mounted)
        {
            return mounted.Error;
        }

        var item = playlist.Mpls.Items[0];
        var size = analysis.ClipSizes.GetValueOrDefault(item.Clip);
        if (size > MaxIsoClip)
        {
            return $"Clip {item.Clip} is {size / 1e9:0.#} GB: too big to stream out of the ISO for pictures.";
        }

        var length = item.Duration45k / (double)Mpls.ClockRate;
        var offset = Offset(item, ClipStarts(analysis));
        var points = Positions.Select(p => (offset + p * length).ToString("0.###", CultureInfo.InvariantCulture)).ToArray();
        // One picture past each point: the first frame after T1, then after T2, then after T3.
        var select = $"select='isnan(prev_selected_t)*gte(t\\,{points[0]})" +
                     $"+gte(prev_selected_t\\,{points[0]})*lt(prev_selected_t\\,{points[1]})*gte(t\\,{points[1]})" +
                     $"+gte(prev_selected_t\\,{points[1]})*lt(prev_selected_t\\,{points[2]})*gte(t\\,{points[2]})'";

        var unpack = new ProcessStartInfo(options.Value.Tools.SevenZip)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in new[] { "e", "-so", source.Path, $"BDMV/STREAM/{item.Clip}.m2ts" })
        {
            unpack.ArgumentList.Add(argument);
        }

        using var sevenZip = Process.Start(unpack) ?? throw new InvalidOperationException("7-Zip did not start.");
        sevenZip.ErrorDataReceived += (_, _) => { };
        sevenZip.BeginErrorReadLine();
        try
        {
            return await RunFfmpegAsync(["-skip_frame", "nokey", "-i", "pipe:0", "-vf", select, "-fps_mode", "vfr", "-frames:v", "3"],
                Path.Combine(folder, "%d.jpg"), sevenZip.StandardOutput.BaseStream, renumber: folder);
        }
        finally
        {
            if (!sevenZip.HasExited)
            {
                sevenZip.Kill();
            }
        }
    }

    /// <summary>
    /// With the mount service, an ISO is read like a folder: ffmpeg seeks, so any clip size works.
    /// Uses a running job's mount of the same ISO, else mounts it and unmounts it after (unless a
    /// job started on it meanwhile). Null when the service could not mount it.
    /// </summary>
    private async Task<(string? Error, bool Done)?> FromMountedIsoAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, string folder)
    {
        var settings = options.Value.IsoMount;
        if (!settings.Enabled)
        {
            return null;
        }

        bool JobUsesIt() => store.List(50, [Jobs.JobStatus.Running]).Any(j => j.SourcePath == source.Path);

        var hostPath = store.List(50, [Jobs.JobStatus.Running]).FirstOrDefault(j => j.SourcePath == source.Path && j.MountedAt != null)?.MountedAt;
        var ours = false;
        if (hostPath == null)
        {
            var reply = await IsoMount.MountAsync(settings.Socket, settings.ToHost(source.Path), CancellationToken.None);
            if (reply is not { Ok: true, Path: { } path })
            {
                return null;
            }

            hostPath = path;
            ours = true;
        }

        try
        {
            var disc = SourceFinder.BdmvParent(settings.ToContainer(hostPath));
            if (disc == null)
            {
                return null;
            }

            var mountedSource = source with { Path = disc, Kind = SourceKind.Folder };
            return (await FromFolderAsync(mountedSource, analysis, playlist, folder), true);
        }
        finally
        {
            if (ours && !JobUsesIt())
            {
                await IsoMount.UnmountAsync(settings.Socket, hostPath, CancellationToken.None);
            }
        }
    }

    /// <summary>Runs ffmpeg with the scaling and output added; null when it worked, else its last line.</summary>
    private async Task<string?> RunFfmpegAsync(string[] input, string output, Stream? stdin, string? renumber = null)
    {
        var start = new ProcessStartInfo(options.Value.Tools.FFmpeg)
        {
            RedirectStandardInput = stdin != null, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        List<string> arguments = ["-hide_banner", "-loglevel", "error"];
        if (stdin == null)
        {
            arguments.Add("-nostdin");
        }

        arguments.AddRange(input);
        var filter = arguments.IndexOf("-vf");
        if (filter >= 0)
        {
            arguments[filter + 1] += ",scale=-2:180";
        }
        else
        {
            arguments.AddRange(["-vf", "scale=-2:180"]);
        }

        arguments.AddRange(["-q:v", "5", "-y", output]);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        }
        catch (Win32Exception)
        {
            return "ffmpeg is not installed.";
        }

        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (stdin != null)
            {
                try
                {
                    await stdin.CopyToAsync(process.StandardInput.BaseStream);
                }
                catch (IOException)
                {
                    // ffmpeg had its three pictures and stopped reading.
                }

                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return "ffmpeg took too long.";
            }

            if (renumber != null)
            {
                // ffmpeg counts from 1: 1.jpg 2.jpg 3.jpg -> 0.jpg 1.jpg 2.jpg.
                for (var i = 1; i <= Count; i++)
                {
                    var from = Path.Combine(renumber, $"{i}.jpg");
                    if (File.Exists(from))
                    {
                        File.Move(from, Path.Combine(renumber, $"{i - 1}.jpg.tmp"), overwrite: true);
                    }
                }

                for (var i = 0; i < Count; i++)
                {
                    var from = Path.Combine(renumber, $"{i}.jpg.tmp");
                    if (File.Exists(from))
                    {
                        File.Move(from, Path.Combine(renumber, $"{i}.jpg"), overwrite: true);
                    }
                }
            }

            var text = (await errors).Trim();
            return process.ExitCode == 0 ? null : text.Split('\n').LastOrDefault() ?? $"ffmpeg exit code {process.ExitCode}";
        }
    }
}
