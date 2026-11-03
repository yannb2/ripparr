using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ripparr.Disc;

namespace Ripparr.Media;

/// <summary>What mediainfo says about one track: enough to name it the way Radarr would.</summary>
public sealed record StreamFacts
{
    public StreamKind Kind { get; init; }
    public string? Format { get; init; }
    public string? Commercial { get; init; }
    public string? Features { get; init; }
    public int Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? FrameRate { get; init; }
    public long FrameCount { get; init; }
    public double DurationSeconds { get; init; }
    public string? HdrFormat { get; init; }
    public string? HdrCompatibility { get; init; }
    public string? HdrSettings { get; init; }
    public string? HdrProfile { get; init; }
    public string? Transfer { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }

    /// <summary>Bits per second as mediainfo reads it; 0 when it gives none (often for raw streams).</summary>
    public long BitRate { get; init; }

    /// <summary>VBR or CBR.</summary>
    public string? BitRateMode { get; init; }

    public long BitRateMaximum { get; init; }

    /// <summary>Bytes of this stream; 0 when mediainfo gives none.</summary>
    public long StreamSize { get; init; }

    /// <summary>Hz (48000), audio only.</summary>
    public int SamplingRate { get; init; }

    /// <summary>10 for 10-bit video, 24 for 24-bit audio.</summary>
    public int BitDepth { get; init; }

    public string? ChromaSubsampling { get; init; }

    /// <summary>Subtitle events (captions), when mediainfo counts them.</summary>
    public int ElementCount { get; init; }

    /// <summary>
    /// The bit rate to show: mediainfo's, or else worked out from the file size and the duration.
    /// </summary>
    public long BitRateFor(long fileSize) =>
        BitRate > 0 ? BitRate : fileSize > 0 && DurationSeconds > 0 ? (long)(fileSize * 8 / DurationSeconds) : 0;

    /// <summary>
    /// The facts that are not in the codec column: picture size, frame rate, bit depth and HDR
    /// for video; sample rate and bit depth for audio; the number of captions for subtitles.
    /// </summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            switch (Kind)
            {
                case StreamKind.Video:
                    if (Width > 0 && Height > 0)
                    {
                        parts.Add($"{Width}x{Height}");
                    }

                    if (double.TryParse(FrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0)
                    {
                        parts.Add(fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
                    }

                    if (BitDepth > 0)
                    {
                        parts.Add($"{BitDepth}-bit");
                    }

                    if (!string.IsNullOrEmpty(ChromaSubsampling))
                    {
                        parts.Add(ChromaSubsampling);
                    }

                    if (DolbyVisionProfile is { } profile)
                    {
                        parts.Add($"DV profile {profile}" + (DualLayer ? " (BL+EL)" : ""));
                    }

                    break;
                case StreamKind.Audio:
                    if (SamplingRate > 0)
                    {
                        parts.Add((SamplingRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz");
                    }

                    if (BitDepth > 0)
                    {
                        parts.Add($"{BitDepth}-bit");
                    }

                    if (Channels > 0)
                    {
                        parts.Add($"{Channels} ch");
                    }

                    break;
                case StreamKind.Subtitle:
                    if (!string.IsNullOrEmpty(Format))
                    {
                        parts.Add(Format);
                    }

                    if (ElementCount > 0)
                    {
                        parts.Add($"{ElementCount} captions");
                    }

                    break;
            }

            if (!string.IsNullOrEmpty(BitRateMode))
            {
                parts.Add(BitRateMode);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Radarr's words: TrueHD Atmos, DTS-HD MA, DTS-X, EAC3 Atmos, AC3, PCM, FLAC...</summary>
    public string AudioCodec
    {
        get
        {
            var format = Format ?? "";
            var commercial = Commercial ?? "";
            var features = Features ?? "";
            if (format.Contains("MLP", StringComparison.OrdinalIgnoreCase) || commercial.Contains("TrueHD", StringComparison.OrdinalIgnoreCase))
            {
                return commercial.Contains("Atmos", StringComparison.OrdinalIgnoreCase) || features.Contains("16-ch", StringComparison.OrdinalIgnoreCase)
                    ? "TrueHD Atmos"
                    : "TrueHD";
            }

            if (format.StartsWith("DTS", StringComparison.OrdinalIgnoreCase))
            {
                if (features.Contains("XLL X", StringComparison.OrdinalIgnoreCase) || commercial.Contains("DTS:X", StringComparison.OrdinalIgnoreCase))
                {
                    return "DTS-X";
                }

                if (features.Contains("XLL", StringComparison.OrdinalIgnoreCase) || commercial.Contains("Master Audio", StringComparison.OrdinalIgnoreCase))
                {
                    return "DTS-HD MA";
                }

                if (features.Contains("XBR", StringComparison.OrdinalIgnoreCase) || commercial.Contains("High Resolution", StringComparison.OrdinalIgnoreCase))
                {
                    return "DTS-HD HRA";
                }

                return features.Contains("ES", StringComparison.Ordinal) ? "DTS-ES" : "DTS";
            }

            if (format.Equals("E-AC-3", StringComparison.OrdinalIgnoreCase))
            {
                return features.Contains("JOC", StringComparison.OrdinalIgnoreCase) || commercial.Contains("Atmos", StringComparison.OrdinalIgnoreCase)
                    ? "EAC3 Atmos"
                    : "EAC3";
            }

            return format.ToUpperInvariant() switch
            {
                "AC-3" => "AC3",
                "PCM" => "PCM",
                "FLAC" => "FLAC",
                "AAC" => "AAC",
                "OPUS" => "Opus",
                "MPEG AUDIO" => "MP2",
                _ => format
            };
        }
    }

    /// <summary>7.1, 5.1, 2.0: the channels with the LFE counted after the dot.</summary>
    public string ChannelWords
    {
        get
        {
            if (Channels <= 0)
            {
                return "";
            }

            var lfe = (ChannelLayout ?? "").Split(' ').Any(p => p.StartsWith("LFE", StringComparison.OrdinalIgnoreCase));
            if (!lfe && ChannelLayout == null && Channels is 6 or 8)
            {
                lfe = true; // no layout given: 6 and 8 channels are 5.1 and 7.1 on a disc
            }

            return lfe ? $"{Channels - 1}.1" : $"{Channels}.0";
        }
    }

    /// <summary>HEVC, AVC, VC-1, MPEG2.</summary>
    public string VideoCodec => (Format ?? "").ToUpperInvariant() switch
    {
        "HEVC" => "HEVC",
        "AVC" => "AVC",
        "VC-1" => "VC-1",
        "MPEG VIDEO" => "MPEG2",
        "AV1" => "AV1",
        var other => other
    };

    /// <summary>2160p, 1080p... from the picture size (a 1920x800 film is still 1080p).</summary>
    public string Resolution => Width switch
    {
        >= 3200 => "2160p",
        >= 1800 => "1080p",
        >= 1200 => "720p",
        _ when Height > 480 => "576p",
        > 0 => "480p",
        _ => ""
    };

    public bool DolbyVision => (HdrFormat ?? "").Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Dolby Vision stream that carries its enhancement layer (BL+EL+RPU).</summary>
    public bool DualLayer => (HdrSettings ?? "").Contains("EL", StringComparison.Ordinal) && DolbyVision;

    /// <summary>The Dolby Vision profile number ("7", "8"), from dvhe.07.06.</summary>
    public string? DolbyVisionProfile
    {
        get
        {
            var profile = HdrProfile ?? "";
            var at = profile.IndexOf("dvhe.", StringComparison.OrdinalIgnoreCase);
            return at >= 0 && profile.Length >= at + 7 && int.TryParse(profile.AsSpan(at + 5, 2), out var p) ? p.ToString(CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>As Radarr's MediaInfo VideoDynamicRangeType: DV HDR10, DV, HDR10Plus, HDR10, HLG, PQ, or empty for SDR.</summary>
    public string DynamicRange
    {
        get
        {
            var format = HdrFormat ?? "";
            var compatibility = HdrCompatibility ?? "";
            var hdr10Plus = format.Contains("2094", StringComparison.Ordinal) || compatibility.Contains("HDR10+", StringComparison.Ordinal);
            var hdr10 = compatibility.Contains("HDR10", StringComparison.Ordinal) || format.Contains("2086", StringComparison.Ordinal);
            if (DolbyVision)
            {
                if (hdr10Plus)
                {
                    return "DV HDR10Plus";
                }

                return hdr10 ? "DV HDR10" : "DV";
            }

            if (hdr10Plus)
            {
                return "HDR10Plus";
            }

            if (hdr10)
            {
                return "HDR10";
            }

            var transfer = Transfer ?? "";
            if (transfer.Contains("HLG", StringComparison.OrdinalIgnoreCase))
            {
                return "HLG";
            }

            return transfer.Contains("PQ", StringComparison.Ordinal) ? "PQ" : "";
        }
    }
}

/// <summary>Reads <c>mediainfo --Output=JSON</c> into one <see cref="StreamFacts"/> per track.</summary>
public static class MediaInfoReader
{
    public static List<StreamFacts> Parse(string? json)
    {
        var list = new List<StreamFacts>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        Root? root;
        try
        {
            root = JsonSerializer.Deserialize<Root>(json);
        }
        catch (JsonException)
        {
            return list;
        }

        foreach (var track in root?.Media?.Tracks ?? [])
        {
            var kind = track.Type switch
            {
                "Video" => StreamKind.Video,
                "Audio" => StreamKind.Audio,
                "Text" => StreamKind.Subtitle,
                _ => StreamKind.Other
            };
            if (kind == StreamKind.Other)
            {
                continue;
            }

            list.Add(new StreamFacts
            {
                Kind = kind,
                Format = Get(track, "Format"),
                Commercial = Get(track, "Format_Commercial_IfAny") ?? Get(track, "Format_Commercial"),
                Features = Get(track, "Format_AdditionalFeatures"),
                Channels = Int(Get(track, "Channels")),
                ChannelLayout = Get(track, "ChannelLayout"),
                Width = Int(Get(track, "Width")),
                Height = Int(Get(track, "Height")),
                FrameRate = Get(track, "FrameRate"),
                FrameCount = Long(Get(track, "FrameCount")),
                DurationSeconds = double.TryParse(Get(track, "Duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
                HdrFormat = Get(track, "HDR_Format"),
                HdrCompatibility = Get(track, "HDR_Format_Compatibility"),
                HdrSettings = Get(track, "HDR_Format_Settings"),
                HdrProfile = Get(track, "HDR_Format_Profile"),
                Transfer = Get(track, "transfer_characteristics"),
                Language = Get(track, "Language"),
                Title = Get(track, "Title"),
                BitRate = Long(Get(track, "BitRate")),
                BitRateMode = Get(track, "BitRate_Mode")?.Split('/')[0].Trim(),
                BitRateMaximum = Long(Get(track, "BitRate_Maximum")),
                StreamSize = Long(Get(track, "StreamSize")),
                SamplingRate = Int(Get(track, "SamplingRate")),
                BitDepth = Int(Get(track, "BitDepth")),
                ChromaSubsampling = Get(track, "ChromaSubsampling"),
                ElementCount = Int(Get(track, "ElementCount") ?? Get(track, "Count_Elements"))
            });
        }

        return list;
    }

    private static int Int(string? value)
    {
        // "8 / 6" for a TrueHD track with its AC-3 core: take the first.
        var first = value?.Split('/')[0].Trim();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;
    }

    private static long Long(string? value)
    {
        var first = value?.Split('/')[0].Trim();
        // Some fields come as "48000.000": keep the whole part.
        return long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    private static string? Get(Track track, string key)
    {
        if (track.Fields == null || !track.Fields.TryGetValue(key, out var value))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private sealed class Root
    {
        [JsonPropertyName("media")] public MediaNode? Media { get; set; }
    }

    private sealed class MediaNode
    {
        [JsonPropertyName("track")] public List<Track>? Tracks { get; set; }
    }

    private sealed class Track
    {
        [JsonPropertyName("@type")] public string? Type { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement>? Fields { get; set; }
    }
}
