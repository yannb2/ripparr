using Ripparr.Disc;

namespace Ripparr.Tests;

/// <summary>The tests' playlists: <see cref="MplsWriter"/> plus ready-made films.</summary>
internal sealed class MplsBuilder : MplsWriter
{
    public static MplsBuilder Feature(params (string Clip, double Seconds)[] clips)
    {
        var builder = new MplsBuilder();
        builder.AddClips(clips);
        return builder;
    }

    /// <summary>A UHD film: HEVC 2160p with a DV layer, TrueHD and AC-3 English, DTS-HD MA French, English and French PGS.</summary>
    public static MplsBuilder UhdFilm(double minutes = 120)
    {
        var builder = Feature(("00055", minutes * 60 * 0.5), ("00056", minutes * 60 * 0.5));
        builder.Video.Add(new Stream(0x24, 0x1011, Format: 8, Rate: 1));
        builder.Audio.Add(new Stream(0x83, 0x1100, "eng", Format: 6, Rate: 1));
        builder.Audio.Add(new Stream(0x81, 0x1101, "eng", Format: 6, Rate: 1));
        builder.Audio.Add(new Stream(0x86, 0x1102, "fra", Format: 6, Rate: 1));
        builder.Audio.Add(new Stream(0x81, 0x1103, "eng", Format: 3, Rate: 1));
        builder.Subtitles.Add(new Stream(0x90, 0x1200, "eng"));
        builder.Subtitles.Add(new Stream(0x90, 0x1201, "fra"));
        builder.Subtitles.Add(new Stream(0x90, 0x1202, "spa"));
        builder.DolbyVision.Add(new Stream(0x24, 0x1015, Format: 6, Rate: 1));
        builder.Marks.Add((0, 27_000_000));
        builder.Marks.Add((0, 27_000_000 + 600 * 45000));
        builder.Marks.Add((1, 27_000_000));
        builder.Marks.Add((1, 27_000_000 + 900 * 45000));
        return builder;
    }
}
