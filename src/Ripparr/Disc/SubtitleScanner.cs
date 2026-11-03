using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ripparr.Media;
using Ripparr.Options;
using Ripparr.Subtitles;

namespace Ripparr.Disc;

/// <summary>What the full read found for one subtitle track.</summary>
/// <param name="Sdh">The OCR (of a sample, or of every caption after "Read subtitles") found SDH text in enough captions.</param>
/// <param name="Commentary">The OCR found people talking about making the film.</param>
/// <param name="Reason">What the OCR found, or why there was none.</param>
public sealed record ScannedSubtitle(int Pid, int Captions, int Forced, bool Sdh = false, bool Commentary = false, string? Reason = null);

/// <summary>A full read of a playlist's subtitle tracks: running, done or failed.</summary>
public sealed class SubtitleScan
{
    /// <summary>The folder name under the subtitle folder: also the key in the preview's address.</summary>
    public required string Key { get; init; }

    public double Progress { get; set; }
    public bool Running { get; set; }

    /// <summary>What the read is doing now, for the page.</summary>
    public string Stage { get; set; } = "Reading the film";
    public string? Error { get; set; }
    public TimeSpan Took { get; set; }
    public Dictionary<int, ScannedSubtitle> Tracks { get; set; } = new();

    /// <summary>For the preview page's title.</summary>
    public string SourceName { get; set; } = "";

    public string Playlist { get; set; } = "";

    /// <summary>Each subtitle PID's language, as the playlist gives it.</summary>
    public Dictionary<int, string?> Languages { get; set; } = new();

    /// <summary>Every caption has been through the OCR ("Read subtitles"), not only a sample.</summary>
    public bool FullText { get; set; }

    /// <summary>Goes up each time the SDH and commentary guesses change, so the job form ticks them again.</summary>
    public int GuessVersion { get; set; }
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
        if (scan == null || !scan.Tracks.ContainsKey(pid))
        {
            return null;
        }

        var path = Path.Combine(_options.SubtitleFolder, key, $"{pid}.sup");
        return File.Exists(path) ? path : null;
    }

    /// <summary>What the OCR read in every caption of one track, once "Read subtitles" has run; empty before.</summary>
    public Dictionary<int, OcrCaption> Text(string key, int pid)
    {
        if (Find(key) is not { FullText: true } scan || !scan.Tracks.ContainsKey(pid))
        {
            return [];
        }

        var path = Path.Combine(_options.SubtitleFolder, key, $"{pid}.ocr.json");
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<OcrCaption>>(File.ReadAllText(path)) ?? []).ToDictionary(c => c.Index)
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            logger.LogWarning(e, "Could not read {File}", path);
            return [];
        }
    }

    /// <summary>
    /// "Read subtitles": the OCR of every caption of every track a preview pulled out, then the
    /// SDH and commentary guesses again on all of them. Runs in the background like the preview.
    /// </summary>
    public SubtitleScan? StartFullText(string key)
    {
        if (Find(key) is not { Running: false, Error: null } scan)
        {
            return null;
        }

        scan.Running = true;
        scan.Progress = 0;
        scan.Stage = "Reading the text of every caption";
        _ = Task.Run(() => FullTextAsync(scan));
        return scan;
    }

    private async Task FullTextAsync(SubtitleScan scan)
    {
        await _one.WaitAsync();
        var watch = Stopwatch.StartNew();
        var folder = Path.Combine(_options.SubtitleFolder, scan.Key);
        try
        {
            var installed = await TesseractLanguagesAsync();
            var read = scan.Tracks.Keys.ToDictionary(p => p, p => PgsReader.Read(Path.Combine(folder, $"{p}.sup")));
            var total = Math.Max(1, read.Values.Sum(c => c.Count));
            var done = 0;
            foreach (var pid in scan.Tracks.Keys.ToList())
            {
                var captions = read[pid];
                var before = done;
                var guess = await GuessAsync(folder, pid, captions, scan.Languages.GetValueOrDefault(pid), installed,
                    MoreThanFirstOfLanguage(scan, pid), all: true, p => scan.Progress = (before + p * captions.Count) / total);
                scan.Tracks[pid] = scan.Tracks[pid] with { Sdh = guess.Sdh, Commentary = guess.Commentary, Reason = guess.Reason };
                done += captions.Count;
                scan.Progress = done / (double)total;
            }

            scan.FullText = true;
            scan.GuessVersion++;
            scan.Took += watch.Elapsed;
            scan.Running = false;
            await File.WriteAllTextAsync(Path.Combine(folder, Done), JsonSerializer.Serialize(scan));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the text of the subtitles in {Folder}", folder);
            scan.Stage = $"The OCR failed: {e.Message}";
        }
        finally
        {
            scan.Running = false;
            _one.Release();
        }
    }

    /// <summary>Starts the read in the background, or hands back the one already running or done.</summary>
    public SubtitleScan Start(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist)
    {
        var key = KeyFor(source, playlist);
        if (Find(key) is { Error: null } existing)
        {
            return existing;
        }

        var scan = new SubtitleScan
        {
            Key = key, Running = true, SourceName = source.Name, Playlist = playlist.Name,
            Languages = playlist.Mpls.Streams.Where(s => s.Kind == StreamKind.Subtitle && s.Pid > 0)
                .GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.First().Language)
        };
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

            var read = pids.ToDictionary(p => p, p => PgsReader.Read(Path.Combine(folder, $"{p}.sup")));
            scan.Stage = "Reading the text of a sample of captions";
            var languages = await TesseractLanguagesAsync();
            var done = 0;
            foreach (var pid in pids)
            {
                var captions = read[pid];
                var language = playlist.Mpls.Streams.First(s => s.Pid == pid).Language;
                var guess = await GuessAsync(folder, pid, captions, language, languages,
                    MoreThanFirstOfLanguage(pids.Select(p => (p, scan.Languages.GetValueOrDefault(p), read[p].Count)).ToList(), pid));
                scan.Tracks[pid] = new ScannedSubtitle(pid, captions.Count, captions.Count(c => c.Forced), guess.Sdh, guess.Commentary, guess.Reason);
                scan.Progress = 0.9 + 0.1 * ++done / pids.Count;
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

    /// <summary>
    /// OCR of a sample of the track's captions, or of all of them (kept as PID.ocr.json for the
    /// preview), then what it looks like (SDH, commentary).
    /// </summary>
    private async Task<TrackGuess> GuessAsync(string folder, int pid, List<PgsCaption> captions, string? language,
        IReadOnlySet<string>? installed, bool moreThanFirst, bool all = false, Action<double>? progress = null)
    {
        var tesseract = SubtitleOcr.TesseractLanguage(language);
        if (captions.Count == 0)
        {
            return new TrackGuess(false, false, "No captions");
        }

        if (installed == null)
        {
            return new TrackGuess(false, false, "Tesseract is not installed: no SDH or commentary check");
        }

        if (!installed.Contains(tesseract))
        {
            return new TrackGuess(false, false, $"No OCR model for {Languages.Name(language)}: no SDH or commentary check");
        }

        try
        {
            var sample = all ? captions : SubtitleClassifier.Sample(captions);
            var ocr = await SubtitleOcr.RecogniseAsync(sample, _options.Tools.Tesseract, tesseract, Path.Combine(folder, $"ocr-{pid}"), progress,
                CancellationToken.None);
            if (all)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, $"{pid}.ocr.json"), JsonSerializer.Serialize(ocr));
            }

            var guess = SubtitleClassifier.Guess(ocr, moreThanFirst);
            return guess with { Reason = (all ? "Every caption: " : "A sample: ") + guess.Reason };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            logger.LogWarning(e, "OCR of the sample of PID {Pid} failed", pid);
            return new TrackGuess(false, false, $"OCR failed: {e.Message}");
        }
        finally
        {
            TryDelete(Path.Combine(folder, $"ocr-{pid}"));
        }
    }

    /// <summary>A later track in the same language with a third more captions than the first one, tracks in disc order.</summary>
    public static bool MoreThanFirstOfLanguage(IReadOnlyList<(int Pid, string? Language, int Captions)> tracks, int pid)
    {
        var me = tracks.First(t => t.Pid == pid);
        var first = tracks.First(t => t.Language == me.Language);
        return first.Pid != pid && first.Captions > 0 && me.Captions > first.Captions * 4 / 3;
    }

    private static bool MoreThanFirstOfLanguage(SubtitleScan scan, int pid) =>
        MoreThanFirstOfLanguage(scan.Tracks.Values.Select(t => (t.Pid, scan.Languages.GetValueOrDefault(t.Pid), t.Captions)).ToList(), pid);

    private IReadOnlySet<string>? _tesseractLanguages;

    /// <summary>The language models Tesseract has (tesseract --list-langs), or null without Tesseract.</summary>
    private async Task<IReadOnlySet<string>?> TesseractLanguagesAsync()
    {
        if (_tesseractLanguages != null)
        {
            return _tesseractLanguages;
        }

        var result = await Tools.ProcessRunner.RunAsync(_options.Tools.Tesseract, ["--list-langs"], TimeSpan.FromSeconds(30));
        if (!result.Success)
        {
            return null;
        }

        return _tesseractLanguages = (result.Output + "\n" + result.Error).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.Contains(' ') && !l.EndsWith(':'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            scan.Progress = total == 0 ? 0 : 0.9 * Math.Min(1, (double)done / total);
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
