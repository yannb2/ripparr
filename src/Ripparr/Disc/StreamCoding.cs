namespace Ripparr.Disc;

public enum StreamKind
{
    Video,
    Audio,
    Subtitle,
    Other
}

/// <summary>The stream coding types of a Blu-ray playlist (the STN table), in words.</summary>
public static class StreamCoding
{
    public static StreamKind Kind(byte coding) => coding switch
    {
        0x01 or 0x02 or 0x1B or 0xEA or 0x24 => StreamKind.Video,
        0x03 or 0x04 or 0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2 => StreamKind.Audio,
        0x90 or 0x92 => StreamKind.Subtitle,
        _ => StreamKind.Other
    };

    /// <summary>Codec words as Radarr writes them in file names (HEVC, TrueHD, DTS-HD MA...).</summary>
    public static string Name(byte coding) => coding switch
    {
        0x01 => "MPEG1",
        0x02 => "MPEG2",
        0x1B => "AVC",
        0xEA => "VC-1",
        0x24 => "HEVC",
        0x03 or 0x04 => "MP2",
        0x80 => "PCM",
        0x81 => "AC3",
        0x82 => "DTS",
        0x83 => "TrueHD",
        0x84 or 0xA1 => "EAC3",
        0x85 => "DTS-HD HRA",
        0x86 => "DTS-HD MA",
        0xA2 => "DTS Express",
        0x90 => "PGS",
        0x91 => "IG",
        0x92 => "Text",
        _ => $"0x{coding:X2}"
    };

    /// <summary>Audio presentation type: 1 mono, 3 stereo, 6 multichannel, 12 stereo + multichannel.</summary>
    public static string? AudioLayout(byte presentation) => presentation switch
    {
        1 => "1.0",
        3 => "2.0",
        6 => "Multi",
        12 => "2.0 + Multi",
        _ => null
    };

    /// <summary>Video format code: 1 480i, 2 576i, 3 480p, 4 1080i, 5 720p, 6 1080p, 7 576p, 8 2160p.</summary>
    public static string? Resolution(byte format) => format switch
    {
        1 => "480i",
        2 => "576i",
        3 => "480p",
        4 => "1080i",
        5 => "720p",
        6 => "1080p",
        7 => "576p",
        8 => "2160p",
        _ => null
    };

    /// <summary>Video frame rate code, as a fraction mkvmerge takes for --default-duration (24000/1001p).</summary>
    public static (int Numerator, int Denominator)? FrameRate(byte rate) => rate switch
    {
        1 => (24000, 1001),
        2 => (24, 1),
        3 => (25, 1),
        4 => (30000, 1001),
        6 => (50, 1),
        7 => (60000, 1001),
        _ => null
    };
}
