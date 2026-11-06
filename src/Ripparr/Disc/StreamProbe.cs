using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Ripparr.Media;
using Ripparr.Options;
using Ripparr.Tools;

namespace Ripparr.Disc;

/// <summary>What the quick read found for one stream: mediainfo's facts and its share of the clips.</summary>
public sealed record ProbedStream(int Pid, StreamFacts? Facts, long Size, long BitRate);

/// <param name="Estimated">True when the sizes come from the start of the film only (an ISO).</param>
public sealed record ProbeResult(IReadOnlyDictionary<int, ProbedStream> Streams, string? Error, bool Estimated, TimeSpan Took)
{
    public static ProbeResult Failed(string error) => new(new Dictionary<int, ProbedStream>(), error, false, TimeSpan.Zero);
}

/// <summary>
/// A quick read of a playlist's streams before the job: mediainfo on the main clip (or on a
/// slice of it read out of an ISO) for codec, channels, bit depth and HDR, and the transport
/// stream packets counted per PID in samples spread over the clips for each track's size and
/// bit rate. An encrypted disc cannot be read this way: its packets only make sense once
/// MakeMKV has decrypted them.
/// </summary>
public sealed class StreamProbe(IOptions<RipparrOptions> options, ILogger<StreamProbe> logger)
{
    private const int PacketSize = 192;
    private const int ChunkSize = 32 * EncryptionCheck.AlignedUnit * 5; // 960 KiB, whole aligned units
    private const int Chunks = 160;
    private const long IsoSlice = 384L * 1024 * 1024;
    private const double PayloadShare = 184.0 / 192;

    private readonly ConcurrentDictionary<string, ProbeResult> _cache = new();
    private readonly RipparrOptions _options = options.Value;

    public ProbeResult? Cached(DiscSource source, PlaylistInfo playlist) =>
        _cache.TryGetValue(Key(source, playlist), out var result) ? result : null;

    public async Task<ProbeResult> ProbeAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, CancellationToken cancellationToken)
    {
        var key = Key(source, playlist);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var watch = Stopwatch.StartNew();
        ProbeResult result;
        try
        {
            result = source.Kind == SourceKind.Iso
                ? await ProbeIsoAsync(source, analysis, playlist, cancellationToken)
                : await ProbeFolderAsync(source, analysis, playlist, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(e, "Could not read the streams of {Path} {Playlist}", source.Path, playlist.Name);
            result = ProbeResult.Failed(e.Message);
        }

        result = result with { Took = watch.Elapsed };
        if (result.Error == null)
        {
            _cache[key] = result;
        }

        return result;
    }

    private async Task<ProbeResult> ProbeFolderAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, CancellationToken cancellationToken)
    {
        var clips = playlist.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Clip: c, Path: DiscAnalyzer.ClipPath(source.Path, c)))
            .Where(c => c.Path != null)
            .Select(c => (c.Clip, Path: c.Path!, Size: new FileInfo(c.Path!).Length))
            .ToList();
        if (clips.Count == 0)
        {
            return ProbeResult.Failed("The playlist's clips are not in BDMV/STREAM.");
        }

        var main = clips.MaxBy(c => c.Size);
        if (EncryptionCheck.Check(main.Path) == Encryption.Encrypted)
        {
            return ProbeResult.Failed(EncryptedMessage);
        }

        // Samples spread over every clip, as many as its share of the playlist.
        var total = clips.Sum(c => c.Size);
        var counts = new Dictionary<int, long>();
        long counted = 0;
        foreach (var clip in clips)
        {
            var samples = Math.Max(1, (int)Math.Round(Chunks * (double)clip.Size / total));
            await using var stream = File.OpenRead(clip.Path);
            var clipCounts = await CountAsync(stream, clip.Size, samples, cancellationToken);
            var clipPackets = clipCounts.Values.Sum();
            if (clipPackets == 0)
            {
                continue;
            }

            // Weigh each clip by its size, not by how many samples it got.
            foreach (var (pid, n) in clipCounts)
            {
                counts[pid] = counts.GetValueOrDefault(pid) + (long)(n * (double)clip.Size / clipPackets);
            }

            counted += clip.Size;
        }

        var facts = await MediaInfoAsync(main.Path, cancellationToken);
        return Build(playlist, facts, counts, counted, estimated: false);
    }

    private async Task<ProbeResult> ProbeIsoAsync(DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, CancellationToken cancellationToken)
    {
        var clips = playlist.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var main = clips.MaxBy(c => analysis.ClipSizes.GetValueOrDefault(c));
        if (main == null)
        {
            return ProbeResult.Failed("The playlist lists no clips.");
        }

        var (entries, error) = await new IsoReader(_options.Tools.SevenZip).ListAsync(source.Path, cancellationToken);
        var entry = entries?.FirstOrDefault(e => !e.Folder && e.Path.EndsWith($"STREAM/{main}.m2ts", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return ProbeResult.Failed(error ?? $"{main}.m2ts is not in the ISO.");
        }

        var folder = Path.Combine(_options.WorkFolder, "probe");
        Directory.CreateDirectory(folder);
        var slice = Path.Combine(folder, $"{Guid.NewGuid():N}.m2ts");
        try
        {
            var (length, encrypted) = await SliceAsync(source.Path, entry.Path, slice, Math.Min(IsoSlice, entry.Size), cancellationToken);
            if (encrypted)
            {
                return ProbeResult.Failed(EncryptedMessage);
            }

            if (length < EncryptionCheck.AlignedUnit * 8)
            {
                return ProbeResult.Failed($"7-Zip gave only {length} bytes of {main}.m2ts.");
            }

            await using var stream = File.OpenRead(slice);
            var counts = await CountAsync(stream, length, 1, cancellationToken, whole: true);
            var facts = await MediaInfoAsync(slice, cancellationToken);
            var total = clips.Sum(c => analysis.ClipSizes.GetValueOrDefault(c));
            var packets = counts.Values.Sum();
            var scaled = packets == 0
                ? counts
                : counts.ToDictionary(p => p.Key, p => (long)(p.Value * (double)total / packets));
            return Build(playlist, facts, scaled, total, estimated: true);
        }
        finally
        {
            TryDelete(slice);
        }
    }

    private const string EncryptedMessage =
        "The disc is encrypted: its streams can only be read once MakeMKV has decrypted them (the job's Prepare step).";

    /// <summary>
    /// Bytes per PID: each 192-byte Blu-ray packet (4-byte header, then the 188-byte TS packet
    /// with its 0x47 sync byte) counts for its PID. Samples are whole aligned units, spread
    /// evenly over the stream, or the whole stream.
    /// </summary>
    public static async Task<Dictionary<int, long>> CountAsync(Stream stream, long length, int samples, CancellationToken cancellationToken, bool whole = false)
    {
        var counts = new Dictionary<int, long>();
        var buffer = new byte[ChunkSize];
        var units = length / EncryptionCheck.AlignedUnit;
        var chunkUnits = ChunkSize / EncryptionCheck.AlignedUnit;
        var positions = whole
            ? Enumerable.Range(0, (int)Math.Max(1, (units + chunkUnits - 1) / chunkUnits)).Select(i => (long)i * chunkUnits)
            : Enumerable.Range(0, samples).Select(i => units <= chunkUnits ? 0 : (long)((units - chunkUnits) * (i + 0.5) / samples)).Distinct();
        foreach (var unit in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.CanSeek)
            {
                stream.Position = unit * EncryptionCheck.AlignedUnit;
            }

            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            Count(buffer.AsSpan(0, read - read % PacketSize), counts);
            if (read < buffer.Length)
            {
                break;
            }
        }

        return counts;
    }

    public static void Count(ReadOnlySpan<byte> data, Dictionary<int, long> counts)
    {
        for (var at = 0; at + PacketSize <= data.Length; at += PacketSize)
        {
            if (data[at + 4] != 0x47)
            {
                continue;
            }

            var pid = ((data[at + 5] & 0x1F) << 8) | data[at + 6];
            counts[pid] = counts.GetValueOrDefault(pid) + PacketSize;
        }
    }

    /// <summary>Each playlist stream with its facts (matched by PID) and its share of the bytes.</summary>
    public static ProbeResult Build(PlaylistInfo playlist, IReadOnlyList<StreamFacts> facts, IReadOnlyDictionary<int, long> bytes, long totalBytes, bool estimated)
    {
        var counted = bytes.Values.Sum();
        var seconds = Math.Max(1, playlist.Duration.TotalSeconds);
        var streams = new Dictionary<int, ProbedStream>();
        foreach (var stream in playlist.Mpls.Streams.Where(s => s.Pid > 0))
        {
            var fact = facts.FirstOrDefault(f => f.Id == stream.Pid);
            long size;
            if (fact is { BitRate: > 0, BitRateMode: "CBR" })
            {
                // A constant rate (AC-3, DTS core, PCM): exact, where counting packets adds the
                // transport stream's headers and padding.
                size = (long)(fact.BitRate * seconds / 8);
            }
            else
            {
                // The packets' payload: 184 of every 192 bytes.
                size = counted == 0 ? 0 : (long)(bytes.GetValueOrDefault(stream.Pid) * (double)totalBytes / counted * PayloadShare);
            }

            streams[stream.Pid] = new ProbedStream(stream.Pid, fact, size, (long)(size * 8 / seconds));
        }

        return new ProbeResult(streams, null, estimated, TimeSpan.Zero);
    }

    /// <summary>
    /// What the job form shows for a stream once it has been read: picture size, bit depth, HDR
    /// and Dolby Vision, frame rate for video; channels, sample rate and bit depth for audio; the
    /// number of captions for subtitles. Without facts, what the playlist says.
    /// </summary>
    public static string Describe(PlaylistStream stream, StreamFacts? facts, bool dolbyVisionLayer)
    {
        if (facts == null)
        {
            return StreamCoding.Details(stream, dolbyVisionLayer);
        }

        var parts = new List<string?>();
        switch (stream.Kind)
        {
            case StreamKind.Video when stream.DolbyVisionEnhancementLayer:
                parts.Add("Dolby Vision enhancement layer");
                parts.Add(facts.Width > 0 ? $"{facts.Width}x{facts.Height}" : null);
                parts.Add(facts.DolbyVisionProfile is { } elProfile ? $"profile {elProfile}" : null);
                break;
            case StreamKind.Video:
                parts.Add(facts.Width > 0 ? $"{facts.Width}x{facts.Height}" : StreamCoding.Resolution(stream.Format));
                parts.Add(facts.BitDepth > 0 ? $"{facts.BitDepth}-bit" : null);
                parts.Add(facts.ChromaSubsampling);
                parts.Add(VideoRange(stream, facts, dolbyVisionLayer));
                parts.Add(double.TryParse(facts.FrameRate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps) && fps > 0
                    ? fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " fps"
                    : null);
                break;
            case StreamKind.Audio:
                parts.Add(facts.SamplingRate > 0 ? (facts.SamplingRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz" : StreamCoding.SampleRate(stream.Rate));
                parts.Add(facts.BitDepth > 0 ? $"{facts.BitDepth}-bit" : null);
                break;
            case StreamKind.Subtitle:
                parts.Add(facts.ElementCount > 0 ? $"{facts.ElementCount} elements" : null);
                break;
        }

        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>
    /// HDR10, HDR10+ or SDR from the stream itself, with Dolby Vision in front: on a UHD disc the
    /// base layer reads as plain HDR10 and the Dolby Vision is the separate enhancement layer.
    /// </summary>
    public static string VideoRange(PlaylistStream stream, StreamFacts facts, bool dolbyVisionLayer)
    {
        var range = Profiles.TrackNaming.Range(facts.DynamicRange, dolbyVision: false);
        if (stream.Hdr10Plus && range == "HDR10")
        {
            range = "HDR10+";
        }

        if (!dolbyVisionLayer && !facts.DolbyVision && stream.DynamicRange != 2)
        {
            return range;
        }

        var profile = facts.DolbyVisionProfile ?? (dolbyVisionLayer ? "7" : null);
        return $"DV{(profile == null ? "" : " " + profile)} {range}".Trim();
    }

    /// <summary>The read streams' details and facts on the job's tracks, so the job keeps them too (the dry run names tracks from the facts).</summary>
    public static void Apply(Profiles.TrackPlan plan, PlaylistInfo playlist, ProbeResult result)
    {
        var dolbyVision = playlist.Mpls.HasDolbyVisionEnhancementLayer;
        foreach (var track in plan.Tracks)
        {
            var stream = playlist.Mpls.Streams.ElementAtOrDefault(track.StreamIndex);
            if (stream == null || !result.Streams.TryGetValue(stream.Pid, out var probed))
            {
                continue;
            }

            track.Probed = probed.Facts ?? track.Probed;
            if (Describe(stream, probed.Facts, dolbyVision) is { Length: > 0 } details)
            {
                track.Details = details;
            }
        }
    }

    private async Task<List<StreamFacts>> MediaInfoAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(_options.Tools.MediaInfo, ["--Output=JSON", path], TimeSpan.FromMinutes(2), cancellationToken);
        return result.Success ? MediaInfoReader.Parse(result.Output) : [];
    }

    /// <summary>
    /// The first bytes of a file inside the ISO: 7-Zip writes it to its output, which is cut off.
    /// An encrypted clip is given up after its first aligned units.
    /// </summary>
    private async Task<(long Length, bool Encrypted)> SliceAsync(string iso, string entry, string target, long bytes, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.Tools.SevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "e", "-so", iso, entry })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("7-Zip did not start.");
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        long written = 0;
        try
        {
            await using var output = File.Create(target);
            var buffer = new byte[1 << 20];
            MemoryStream? header = new(EncryptionCheck.AlignedUnit * 8);
            while (written < bytes)
            {
                var n = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes - written)), cancellationToken);
                if (n == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                if (header != null)
                {
                    header.Write(buffer, 0, (int)Math.Min(n, header.Capacity - header.Length));
                    if (header.Length == header.Capacity)
                    {
                        header.Position = 0;
                        if (EncryptionCheck.Check(header) == Encryption.Encrypted)
                        {
                            return (0, true);
                        }

                        header = null;
                    }
                }

                written += n;
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return (written - written % PacketSize, false);
    }

    private static string Key(DiscSource source, PlaylistInfo playlist) => $"{source.Path}|{source.Modified:O}|{playlist.Name}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
