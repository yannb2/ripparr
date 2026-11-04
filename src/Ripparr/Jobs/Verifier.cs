using System.Globalization;
using System.Text.Json;
using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Tools;

namespace Ripparr.Jobs;

/// <summary>What mkvmerge -J says about the finished file.</summary>
public sealed record MkvTrack(StreamKind Kind, string? Language, string? Name, bool Default, bool Forced,
    bool Commentary = false, bool HearingImpaired = false, bool VisualImpaired = false);

public sealed record MkvSummary(List<MkvTrack> Tracks, TimeSpan Duration, int Chapters, string? Title = null);

/// <summary>Reads the finished MKV back and compares it with the plan and the playlist.</summary>
public static class Verifier
{
    public static async Task<List<VerifyCheck>> VerifyAsync(JobContext context, string path, PlaylistInfo playlist,
        IReadOnlyList<OutputTrack> outputs, bool dolbyVisionLayer, string? title = null)
    {
        var tools = context.Options.Tools;
        var checks = new List<VerifyCheck>();

        var identify = await context.RunAsync(new ProcessSpec(tools.MkvMerge, ["-J", path]), TimeSpan.FromMinutes(5), _ => true);
        var summary = ParseIdentify(identify.Output);
        if (summary == null)
        {
            checks.Add(new VerifyCheck("Readable", false, "mkvmerge could not read the finished file."));
            return checks;
        }

        checks.AddRange(Compare(summary, playlist, outputs, title));

        var info = await context.RunAsync(new ProcessSpec(tools.MediaInfo, ["--Output=JSON", path]), TimeSpan.FromMinutes(10), _ => true);
        var video = MediaInfoReader.Parse(info.Output).FirstOrDefault(f => f.Kind == StreamKind.Video);
        var expected = outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Video)?.File.Facts;
        if (video != null && expected != null)
        {
            var wanted = dolbyVisionLayer && !expected.DolbyVision
                ? (expected.DynamicRange.Length > 0 ? "DV " + expected.DynamicRange : "DV")
                : expected.DynamicRange;
            checks.Add(new VerifyCheck("Dynamic range", video.DynamicRange == wanted,
                $"{Or(video.DynamicRange, "SDR")} (expected {Or(wanted, "SDR")})"));
        }

        if (dolbyVisionLayer)
        {
            checks.AddRange(await DolbyVisionChecksAsync(context, path, video));
        }

        return checks;
    }

    /// <summary>
    /// Tracks (count and order), languages, names, flags, the MKV title, duration and chapters
    /// against the plan and the playlist. Names, flags and title only warn: the file plays, a
    /// player just shows or picks something else than planned.
    /// </summary>
    public static List<VerifyCheck> Compare(MkvSummary summary, PlaylistInfo playlist, IReadOnlyList<OutputTrack> outputs,
        string? title = null)
    {
        var checks = new List<VerifyCheck>();
        var counts = new[] { StreamKind.Video, StreamKind.Audio, StreamKind.Subtitle }
            .Select(k => (Kind: k, Want: outputs.Count(o => o.Planned.Kind == k), Got: summary.Tracks.Count(t => t.Kind == k)))
            .ToList();
        checks.Add(new VerifyCheck("Tracks", counts.All(c => c.Want == c.Got),
            string.Join(", ", counts.Select(c => $"{c.Got}/{c.Want} {c.Kind.ToString().ToLowerInvariant()}"))));

        // Muxarr checks this too: each position holds the kind of track the plan put there.
        var misplaced = outputs.Zip(summary.Tracks).Select((p, i) => (p.First, p.Second, Position: i + 1))
            .Where(p => p.First.Planned.Kind != p.Second.Kind)
            .Select(p => $"track {p.Position} is {p.Second.Kind.ToString().ToLowerInvariant()}, planned {p.First.Planned.Kind.ToString().ToLowerInvariant()}")
            .ToList();
        checks.Add(new VerifyCheck("Order", misplaced.Count == 0, misplaced.Count == 0 ? "as planned" : string.Join("; ", misplaced)));

        var wrongLanguages = outputs.Zip(summary.Tracks)
            .Where(p => !Languages.Same(p.First.Planned.Language, p.Second.Language) && p.First.Planned.Language != p.Second.Language)
            .Select(p => $"{p.Second.Name ?? p.Second.Kind.ToString()}: {p.Second.Language} instead of {p.First.Planned.Language}")
            .ToList();
        checks.Add(new VerifyCheck("Languages", wrongLanguages.Count == 0,
            wrongLanguages.Count == 0 ? "as planned" : string.Join("; ", wrongLanguages)));

        var wrongNames = outputs.Zip(summary.Tracks)
            .Where(p => !string.Equals((p.First.Name ?? "").Trim(), (p.Second.Name ?? "").Trim(), StringComparison.Ordinal))
            .Select(p => $"\"{p.Second.Name}\" instead of \"{p.First.Name}\"")
            .ToList();
        checks.Add(new VerifyCheck("Names", true, wrongNames.Count == 0 ? "as planned" : string.Join("; ", wrongNames), Warning: wrongNames.Count > 0));

        var wrongFlags = outputs.Zip(summary.Tracks).Select((p, i) => (Position: i + 1, Words: FlagDifferences(p.First.Planned, p.Second)))
            .Where(p => p.Words.Count > 0)
            .Select(p => $"track {p.Position}: {string.Join(", ", p.Words)}")
            .ToList();
        checks.Add(new VerifyCheck("Flags", true, wrongFlags.Count == 0 ? "as planned" : string.Join("; ", wrongFlags), Warning: wrongFlags.Count > 0));

        if (title != null)
        {
            var same = string.Equals(title.Trim(), (summary.Title ?? "").Trim(), StringComparison.Ordinal);
            checks.Add(new VerifyCheck("Title", true, same ? title : $"\"{summary.Title}\" instead of \"{title}\"", Warning: !same));
        }

        var difference = Math.Abs((summary.Duration - playlist.Duration).TotalSeconds);
        checks.Add(new VerifyCheck("Duration", difference <= 10,
            $"{Format.Duration(summary.Duration)} for a {Format.Duration(playlist.Duration)} playlist ({difference.ToString("0.0", CultureInfo.InvariantCulture)} s apart)",
            Warning: difference is > 2 and <= 10));

        var chapters = playlist.Mpls.Chapters.Count;
        checks.Add(new VerifyCheck("Chapters", summary.Chapters == chapters, $"{summary.Chapters} (playlist has {chapters})"));
        return checks;
    }

    /// <summary>The flags that came out otherwise than planned ("default off, planned on").</summary>
    public static List<string> FlagDifferences(Profiles.PlannedTrack planned, MkvTrack actual)
    {
        var words = new List<string>();
        void Check(string name, bool want, bool got)
        {
            if (want != got)
            {
                words.Add($"{name} {(got ? "on" : "off")}, planned {(want ? "on" : "off")}");
            }
        }

        Check("default", planned.Default, actual.Default);
        if (planned.Kind == StreamKind.Subtitle)
        {
            Check("forced", planned.Forced, actual.Forced);
        }

        Check("commentary", planned.Commentary, actual.Commentary);
        Check("SDH", planned.HearingImpaired, actual.HearingImpaired);
        Check("AD", planned.VisualImpaired, actual.VisualImpaired);
        return words;
    }

    private static async Task<List<VerifyCheck>> DolbyVisionChecksAsync(JobContext context, string path, StreamFacts? video)
    {
        var checks = new List<VerifyCheck>
        {
            new("Dolby Vision layer", video?.DualLayer == true,
                video == null ? "no video track" : $"{Or(video.HdrSettings, "no Dolby Vision")}, profile {Or(video.DolbyVisionProfile, "?")}")
        };

        context.Log("Reading the whole RPU with dovi_tool (a few minutes)...");
        DoviTool.Executable = context.Options.Tools.DoviTool;
        var result = await DoviTool.ReadAsync(path, frames: null, TimeSpan.FromHours(2), context.CancellationToken);
        if (result.Details is not { } details)
        {
            checks.Add(new VerifyCheck("Dolby Vision RPU", false, result.Error ?? "dovi_tool read nothing"));
            return checks;
        }

        checks.Add(new VerifyCheck("Dolby Vision RPU", true,
            $"profile {Or(details.Profile, "?")} {details.Layer} {details.CmVersion}".Trim(), Warning: details.Layer == null));
        if (video is { FrameCount: > 0 })
        {
            checks.Add(new VerifyCheck("RPU frames", details.Frames == video.FrameCount,
                $"{details.Frames} RPU frames for {video.FrameCount} video frames"));
        }

        return checks;
    }

    /// <summary>Reads mkvmerge -J (identify as JSON).</summary>
    public static MkvSummary? ParseIdentify(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tracks = new List<MkvTrack>();
            if (root.TryGetProperty("tracks", out var list))
            {
                foreach (var track in list.EnumerateArray())
                {
                    var kind = track.GetProperty("type").GetString() switch
                    {
                        "video" => StreamKind.Video,
                        "audio" => StreamKind.Audio,
                        "subtitles" => StreamKind.Subtitle,
                        _ => StreamKind.Other
                    };
                    var properties = track.TryGetProperty("properties", out var p) ? p : default;
                    tracks.Add(new MkvTrack(kind,
                        String(properties, "language"),
                        String(properties, "track_name"),
                        Bool(properties, "default_track"),
                        Bool(properties, "forced_track"),
                        Bool(properties, "flag_commentary"),
                        Bool(properties, "flag_hearing_impaired"),
                        Bool(properties, "flag_visual_impaired")));
                }
            }

            long nanoseconds = 0;
            string? title = null;
            if (root.TryGetProperty("container", out var container)
                && container.TryGetProperty("properties", out var containerProperties))
            {
                if (containerProperties.TryGetProperty("duration", out var duration))
                {
                    nanoseconds = duration.GetInt64();
                }

                title = String(containerProperties, "title");
            }

            var chapters = 0;
            if (root.TryGetProperty("chapters", out var chapterList))
            {
                chapters = chapterList.EnumerateArray()
                    .Sum(c => c.TryGetProperty("num_entries", out var n) ? n.GetInt32() : 0);
            }

            return new MkvSummary(tracks, TimeSpan.FromTicks(nanoseconds / 100), chapters, title);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
