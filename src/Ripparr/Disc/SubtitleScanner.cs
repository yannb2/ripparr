using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ripparr.Options;
using Ripparr.Subtitles;

namespace Ripparr.Disc;

/// <summary>What the full read found for one subtitle track.</summary>
public sealed record ScannedSubtitle(int Pid, int Captions, int Forced);

/// <summary>A full read of a playlist's subtitle tracks: running, done or failed.</summary>
public sealed class SubtitleScan
{
    /// <summary>The folder name under the subtitle folder: also the key in the preview's address.</summary>
    public required string Key { get; init; }

    public double Progress { get; set; }
    public bool Running { get; set; }
    public string? Error { get; set; }
    public TimeSpan Took { get; set; }
    public Dictionary<int, ScannedSubtitle> Tracks { get; set; } = new();

    /// <summary>For the preview page's title.</summary>
    public string SourceName { get; set; } = "";

    public string Playlist { get; set; } = "";
}

/// <summary>
/// Reads every byte of a playlist's clips once to pull out its PGS subtitle tracks, before the
/// job: the exact number of captions and forced captions per track, and a .sup per track for the
/// preview page, so the right track can be picked as forced or default. Minutes on a full UHD
/// film (the whole film is read), so it only runs when asked, one at a time, and the result
/// stays in the subtitle folder (disc-*), pruned with the job previews.
/// </summary>
public sealed class SubtitleScanner(IOptions<RipparrOptions> options, ILogger<SubtitleScanner> logger)
{
    private const string Done = "scan.json";
    private readonly RipparrOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, SubtitleScan> _scans = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    public static string KeyFor(DiscSource source, PlaylistInfo playlist)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source.Path}|{source.Modified:O}|{playlist.Name}"));
        return "disc-" + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>A scan running or done for this playlist, also one from before a restart.</summary>
    public SubtitleScan? Find(DiscSource source, PlaylistInfo playlist) => Find(KeyFor(source, playlist));

    public SubtitleScan? Find(string key)
    {
        if (!IsKey(key))
        {
            return null;
        }

        if (_scans.TryGetValue(key, out var scan))
        {
            return scan;
        }

        var file = Path.Combine(_options.SubtitleFolder, key, Done);
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<SubtitleScan>(File.ReadAllText(file)) is { } saved)
            {
                return _scans.GetOrAdd(key, saved);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not read {File}", file);
        }

        return null;
    }

    /// <summary>The .sup of one track of a finished scan; the PID has to be one the scan wrote.</summary>
    public string? SupFile(string key, int pid)
    {
        var scan = Find(key);
        if (scan == null || scan.Running || !scan.Tracks.ContainsKey(pid))
        {
            return null;
        }

        var path = Path.Combine(_options.SubtitleFolder, key, $"{pid}.sup");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Starts the read in the background, or hands back the one already running or done.</summary>
    public SubtitleScan Start(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist)
    {
        var key = KeyFor(source, playlist);
        if (Find(key) is { Error: null } existing)
        {
            return existing;
        }

        var scan = new SubtitleScan { Key = key, Running = true, SourceName = source.Name, Playlist = playlist.Name };
        _scans[key] = scan;
        _ = Task.Run(() => RunAsync(scan, source, analysis, playlist));
        return scan;
    }

    private async Task RunAsync(SubtitleScan scan, DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist)
    {
        await _one.WaitAsync();
        var watch = Stopwatch.StartNew();
        var folder = Path.Combine(_options.SubtitleFolder, scan.Key);
        try
        {
            var pids = playlist.Mpls.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.Coding == 0x90 && s.Pid > 0)
                .Select(s => s.Pid).Distinct().ToList();
            if (pids.Count == 0)
            {
                scan.Error = "This playlist has no picture subtitles (PGS).";
                return;
            }

            Directory.CreateDirectory(folder);
            var outputs = pids.ToDictionary(p => p, p => (Stream)new FileStream(Path.Combine(folder, $"{p}.sup"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16));
            try
            {
                using var extractor = new PgsExtractor(outputs);
                scan.Error = source.Kind == SourceKind.Iso
                    ? await ReadIsoAsync(scan, source, analysis, playlist, extractor)
                    : await ReadFolderAsync(scan, source, playlist, extractor);
                extractor.Finish();
            }
            finally
            {
                foreach (var output in outputs.Values)
                {
                    await output.DisposeAsync();
                }
            }

            if (scan.Error != null)
            {
                return;
            }

            foreach (var pid in pids)
            {
                var captions = PgsReader.Read(Path.Combine(folder, $"{pid}.sup"));
                scan.Tracks[pid] = new ScannedSubtitle(pid, captions.Count, captions.Count(c => c.Forced));
            }

            scan.Progress = 1;
            scan.Took = watch.Elapsed;
            scan.Running = false;
            await File.WriteAllTextAsync(Path.Combine(folder, Done), JsonSerializer.Serialize(scan));
            Prune();
        }
        catch (Exception e)
        {
            // A background read: whatever goes wrong ends up on the page, not in an unobserved task.
            logger.LogWarning(e, "Could not read the subtitles of {Path} {Playlist}", source.Path, playlist.Name);
            scan.Error = e.Message;
        }
        finally
        {
            scan.Running = false;
            scan.Took = watch.Elapsed;
            if (scan.Error != null)
            {
                TryDelete(folder); // the message stays for the page; Start tries again
            }

            _one.Release();
        }
    }

    private static async Task<string?> ReadFolderAsync(SubtitleScan scan, DiscSource source, PlaylistInfo playlist, PgsExtractor extractor)
    {
        var items = playlist.Mpls.Items.Select(i => (Item: i, Path: DiscAnalyzer.ClipPath(source.Path, i.Clip))).ToList();
        if (items.Any(i => i.Path == null))
        {
            return "Some of the playlist's clips are not in BDMV/STREAM.";
        }

        var main = items.MaxBy(i => new FileInfo(i.Path!).Length).Path!;
        if (EncryptionCheck.Check(main) == Encryption.Encrypted)
        {
            return Encrypted;
        }

        var total = items.Sum(i => new FileInfo(i.Path!).Length);
        long done = 0, offset = 0;
        foreach (var (item, path) in items)
        {
            extractor.StartItem(offset, item.InTime, item.OutTime);
            await using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            done = await PumpAsync(stream, extractor, scan, done, total);
            offset += item.Duration45k;
        }

        return null;
    }

    private async Task<string?> ReadIsoAsync(SubtitleScan scan, DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, PgsExtractor extractor)
    {
        var (entries, error) = await new IsoReader(_options.Tools.SevenZip).ListAsync(source.Path, CancellationToken.None);
        if (entries == null)
        {
            return error ?? "7-Zip could not list the ISO.";
        }

        var total = playlist.Mpls.Items.Sum(i => analysis.ClipSizes.GetValueOrDefault(i.Clip));
        long done = 0, offset = 0;
        var first = true;
        foreach (var item in playlist.Mpls.Items)
        {
            var entry = entries.FirstOrDefault(e => !e.Folder && e.Path.EndsWith($"STREAM/{item.Clip}.m2ts", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return $"{item.Clip}.m2ts is not in the ISO.";
            }

            extractor.StartItem(offset, item.InTime, item.OutTime);
            var start = new ProcessStartInfo(_options.Tools.SevenZip) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "e", "-so", source.Path, entry.Path })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new IOException("7-Zip did not start.");
            _ = process.StandardError.ReadToEndAsync();
            try
            {
                var stream = process.StandardOutput.BaseStream;
                if (first)
                {
                    // The first aligned units tell an encrypted disc: no point reading on.
                    var head = new byte[EncryptionCheck.AlignedUnit * 8];
                    var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
                    if (EncryptionCheck.Check(new MemoryStream(head, 0, read)) == Encryption.Encrypted)
                    {
                        return Encrypted;
                    }

                    extractor.Feed(head.AsSpan(0, read - read % 192));
                    done += read;
                    first = false;
                }

                done = await PumpAsync(stream, extractor, scan, done, total);
                await process.WaitForExitAsync();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            offset += item.Duration45k;
        }

        return null;
    }

    private static async Task<long> PumpAsync(Stream stream, PgsExtractor extractor, SubtitleScan scan, long done, long total)
    {
        var buffer = new byte[4 * 1024 * 1024];
        var filled = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(filled));
            if (n == 0)
            {
                break;
            }

            filled += n;
            done += n;
            var whole = filled - filled % 192;
            extractor.Feed(buffer.AsSpan(0, whole));
            buffer.AsSpan(whole, filled - whole).CopyTo(buffer);
            filled -= whole;
            scan.Progress = total == 0 ? 0 : Math.Min(0.99, (double)done / total);
        }

        return done;
    }

    private const string Encrypted =
        "The disc is encrypted: its subtitles can only be read once MakeMKV has decrypted it (the job's Prepare step).";

    private static bool IsKey(string key) => key.Length == 21 && key.StartsWith("disc-", StringComparison.Ordinal) && key[5..].All(char.IsAsciiHexDigitLower);

    /// <summary>Keeps as many disc reads as job previews, newest first.</summary>
    private void Prune()
    {
        try
        {
            var old = new DirectoryInfo(_options.SubtitleFolder).GetDirectories("disc-*")
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(Math.Max(_options.KeepSubtitlePreviews, 1));
            foreach (var dir in old)
            {
                _scans.TryRemove(dir.Name, out _);
                dir.Delete(recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not tidy {Folder}", _options.SubtitleFolder);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
