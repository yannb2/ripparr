using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Ripparr.Jobs;
using Ripparr.Options;

namespace Ripparr.Disc;

/// <summary>
/// A whole playlist played in the browser, to check an extra (or an episode) before ripping it:
/// ffmpeg reads the playlist's clips one after the other (its concat list, cut at each play item's
/// in and out times) and sends a 720p H.264 fragmented MP4 as it goes. Nothing is kept on disk.
/// No seeking in a live stream: the player can start at a quarter, half or three quarters instead.
/// </summary>
public sealed class LivePlay(IOptions<RipparrOptions> options, JobStore store, ILogger<LivePlay> logger)
{
    /// <summary>Streams at once: a new one stops the oldest (a tab left open keeps ffmpeg busy otherwise).</summary>
    public const int MaxStreams = 2;

    /// <summary>Where the whole-playlist player may start, in percent.</summary>
    public static readonly int[] StartPercents = [0, 25, 50, 75];

    private readonly LinkedList<CancellationTokenSource> _running = new();

    /// <summary>
    /// ffmpeg's concat list for the playlist from <paramref name="fromSeconds"/> on. A play item's
    /// in and out times are the clip's own time stamps (45 kHz clock), which is what the concat
    /// demuxer's inpoint and outpoint expect. Null when a clip is missing from the disc.
    /// With <paramref name="streams"/> (PIDs), the list keeps only those streams, in that order: the
    /// concat demuxer does not carry a clip's PIDs over, so the tracks are picked here.
    /// </summary>
    public static string? ConcatList(Mpls playlist, string disc, double fromSeconds = 0, IReadOnlyList<int>? streams = null)
    {
        var text = new StringBuilder("ffconcat version 1.0\n");
        foreach (var pid in streams ?? [])
        {
            text.Append("stream\nexact_stream_id 0x").Append(pid.ToString("x", CultureInfo.InvariantCulture)).Append('\n');
        }

        var start = 0.0;
        foreach (var item in playlist.Items)
        {
            var length = item.Duration45k / (double)Mpls.ClockRate;
            var skip = fromSeconds - start;
            start += length;
            if (skip >= length - 1 && item != playlist.Items[^1])
            {
                continue;
            }

            var path = DiscAnalyzer.ClipPath(disc, item.Clip);
            if (path == null)
            {
                return null;
            }

            var inpoint = item.InTime / (double)Mpls.ClockRate + Math.Clamp(skip, 0, Math.Max(0, length - 1));
            text.Append("file '").Append(path.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n")
                .Append("inpoint ").Append(inpoint.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n')
                .Append("outpoint ").Append((item.OutTime / (double)Mpls.ClockRate).ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The streams the concat list keeps for the job's tracks: the video, the audio, the subtitle
    /// (each only when the playlist has it). Null keeps them all (the first audio, no subtitles).
    /// </summary>
    public static List<int>? Streams(Mpls playlist, WatchTracks tracks)
    {
        if (tracks == WatchTracks.None || WatchTracks.VideoPid(playlist) is not { } video)
        {
            return null;
        }

        return [video, ..tracks.Audio is { } a ? [a] : Array.Empty<int>(), ..tracks.Subtitle is { } s ? [s] : Array.Empty<int>()];
    }

    /// <summary>
    /// ffmpeg's arguments: the concat list in, a fragmented MP4 out on stdout. With the job's
    /// tracks, the list holds video, audio, subtitle in that order (<see cref="Streams"/>).
    /// </summary>
    public static string[] Arguments(string list, WatchTracks? tracks = null)
    {
        tracks ??= WatchTracks.None;
        var picked = tracks != WatchTracks.None;
        var audio = picked ? tracks.Audio != null ? "0:1" : null : "0:a:0?";
        var subtitle = tracks.Subtitle == null ? null : tracks.Audio != null ? "0:2" : "0:1";
        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "concat", "-safe", "0", "-i", list,
            ..WatchTracks.VideoArguments(picked ? "0:0" : "0:v:0", subtitle, "scale='min(1280,iw)':-2"),
            ..audio == null ? Array.Empty<string>() : ["-map", audio],
            "-sn", "-dn", "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-g", "48",
            "-ac", "2", "-c:a", "aac", "-b:a", "160k",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "pipe:1"
        ];
    }

    /// <summary>
    /// Plays the playlist into the response until it ends or the browser lets go. Returns why it
    /// could not start (nothing written yet), or null once the stream ran.
    /// </summary>
    public async Task<string?> StreamAsync(DiscSource source, PlaylistInfo playlist, int fromPercent, WatchTracks tracks, HttpResponse response,
        CancellationToken cancellationToken)
    {
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        LinkedListNode<CancellationTokenSource> node;
        lock (_running)
        {
            while (_running.Count >= MaxStreams && _running.First is { } oldest)
            {
                oldest.Value.Cancel();
                _running.RemoveFirst();
            }

            node = _running.AddLast(mine);
        }

        try
        {
            var from = playlist.Mpls.Duration.TotalSeconds * Math.Clamp(fromPercent, 0, 99) / 100.0;
            if (source.Kind != SourceKind.Iso)
            {
                return await PlayAsync(SourceFinder.BdmvParent(source.Path) ?? source.Path, source, playlist, from, tracks, response, mine.Token);
            }

            var running = store.List(50, [JobStatus.Running]).FirstOrDefault(j => j.SourcePath == source.Path && j.MountedAt != null);
            var played = await IsoMount.BorrowAsync(options.Value.IsoMount, source.Path, running?.MountedAt,
                () => store.List(50, [JobStatus.Running]).Any(j => j.SourcePath == source.Path),
                async disc => new Tuple<string?>(await PlayAsync(disc, source, playlist, from, tracks, response, mine.Token)));
            return played == null ? "Playing an ISO needs the mount service." : played.Item1;
        }
        finally
        {
            lock (_running)
            {
                if (node.List != null)
                {
                    _running.Remove(node);
                }
            }
        }
    }

    private async Task<string?> PlayAsync(string disc, DiscSource source, PlaylistInfo playlist, double from, WatchTracks tracks, HttpResponse response,
        CancellationToken cancellationToken)
    {
        var streams = Streams(playlist.Mpls, tracks);
        tracks = streams == null ? WatchTracks.None : tracks;
        var list = ConcatList(playlist.Mpls, disc, from, streams);
        if (list == null)
        {
            return $"A clip of playlist {playlist.Name} is missing.";
        }

        var folder = Path.Combine(options.Value.ThumbnailFolder, Thumbnails.Key(source, playlist.Name));
        Directory.CreateDirectory(folder);
        var listFile = Path.Combine(folder, $"play-{Guid.NewGuid():N}.ffconcat");
        await File.WriteAllTextAsync(listFile, list, CancellationToken.None);

        var start = new ProcessStartInfo(options.Value.Tools.FFmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in Arguments(listFile, tracks))
        {
            start.ArgumentList.Add(argument);
        }

        Process? ffmpeg = null;
        try
        {
            ffmpeg = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
            var errors = new StringBuilder();
            ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } line && errors.Length < 4000)
                {
                    errors.AppendLine(line);
                }
            };
            ffmpeg.BeginErrorReadLine();

            var output = ffmpeg.StandardOutput.BaseStream;
            var buffer = new byte[256 * 1024];
            var read = await output.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await ffmpeg.WaitForExitAsync(cancellationToken);
                return errors.ToString().Trim().Split('\n').LastOrDefault() is { Length: > 0 } last ? last.Trim() : "ffmpeg could not play the playlist.";
            }

            response.ContentType = "video/mp4";
            response.Headers.CacheControl = "no-store";
            while (read > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                read = await output.ReadAsync(buffer, cancellationToken);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            // The browser closed the player, moved on, or a newer stream took the place.
            return response.HasStarted ? null : "Stopped.";
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(e, "Playing playlist {Playlist} failed", playlist.Name);
            return response.HasStarted ? null : e.Message;
        }
        finally
        {
            if (ffmpeg != null)
            {
                try
                {
                    if (!ffmpeg.HasExited)
                    {
                        ffmpeg.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                ffmpeg.Dispose();
            }

            File.Delete(listFile);
        }
    }
}
