using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Tools;

namespace Ripparr.Jobs;

/// <summary>A track of an MKV as mkvmerge -J lists it.</summary>
public sealed record MkvInputTrack(int Id, int Number, StreamKind Kind, string Codec, string? Language, bool Forced, string? Name);

/// <summary>
/// A DVD job's own steps, up to the tracks: MakeMKV rips the title into an MKV (once per title:
/// the episodes cut from one title share it), mkvmerge cuts the job's chapters out of it, and
/// the DVD's streams are matched to the MKV's tracks. From there the job goes on as a Blu-ray's.
/// </summary>
public static class DvdSteps
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(6);

    /// <summary>One rip of a title at a time, when several jobs want the same one.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Rips = new(StringComparer.Ordinal);

    public static Dictionary<string, double> Weights(bool part) => new()
    {
        [JobSteps.Check] = 1,
        [JobSteps.Prepare] = 40,
        [JobSteps.Demux] = part ? 10 : 0.5,
        [JobSteps.ReadStreams] = 2,
        [JobSteps.Subtitles] = 1,
        [JobSteps.DolbyVision] = 0,
        [JobSteps.Mux] = 20,
        [JobSteps.Verify] = 3,
        [JobSteps.Finish] = 1
    };

    /// <summary>The folder a title's rip is kept in, outside the job folders so the title's other episodes find it.</summary>
    public static string RipFolder(string workFolder, string sourcePath, string title)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)))[..12].ToLowerInvariant();
        return Path.Combine(workFolder, $"dvd-{hash}-{title}");
    }

    /// <summary>The playlist (its chapters as the rip has them) and the tracks, or null after a dry run.</summary>
    public static async Task<(PlaylistInfo Playlist, List<OutputTrack> Outputs)?> RipAsync(JobContext context, DiscSource source,
        DiscAnalysis analysis, PlaylistInfo playlist)
    {
        var job = context.Job;
        var tools = context.Options.Tools;
        job.Dvd = true;
        var title = playlist.PartOf != null ? analysis.Find(playlist.PartOf) ?? playlist : playlist;
        var dvd = playlist.Dvd ?? throw new JobFailedException($"{playlist.Label} is not a DVD title.");
        var part = playlist.PartOf != null;
        context.Log($"DVD {title.Label}: {Format.Duration(title.Duration)}, {title.Mpls.Chapters.Count} chapters, {dvd.Standard} {dvd.Width}x{dvd.Height} {dvd.Aspect}, " +
                    $"{dvd.Audio.Count} audio, {dvd.Subtitles.Count} subtitle stream(s), {Format.Size(title.Size)}" +
                    (part ? $"; this job is chapters {playlist.FirstChapter} to {playlist.LastChapter} ({Format.Duration(playlist.Duration)})." : "."));
        job.StepWeights = Weights(part);
        Directory.CreateDirectory(context.WorkFolder);
        var ripFolder = RipFolder(context.Options.WorkFolder, source.Path, title.Name);
        var ripped = Ripped(ripFolder, source);
        CheckSpace(context, ripped == null ? title.Size : 0, playlist.Size, part);
        context.Save();

        var makemkv = new List<string> { "-r", "--progress=-same", "--noscan", "--minlength=0", "mkv", MakeMkv.SourceArgument(source), "<title>", ripFolder };
        if (job.DryRun)
        {
            context.Step("Dry run");
            context.Log(ripped != null
                ? $"Rip: {ripped} is there already (ripped for another episode of this title)."
                : $"Rip: makemkvcon info finds MakeMKV's number for DVD title {dvd.Number}, then: $ " +
                  new ProcessSpec(tools.MakeMkvCon, makemkv).CommandLine);
            if (part)
            {
                context.Log($"Cut: $ {new ProcessSpec(tools.MkvMerge, CutArguments(Path.Combine(context.DemuxFolder, "part.mkv"), Path.Combine(ripFolder, "title.mkv"),
                    playlist.Mpls.Chapters.Count == 0 ? TimeSpan.Zero : title.Mpls.Chapters[playlist.FirstChapter - 1].Start,
                    playlist.LastChapter < title.Mpls.Chapters.Count ? title.Mpls.Chapters[playlist.LastChapter].Start : null)).CommandLine}");
            }

            context.Log("Then: mkvmerge -J and mediainfo on the MKV, its tracks matched to the DVD's streams, and mkvmerge with these tracks:");
            foreach (var track in job.Plan.Kept)
            {
                context.Log($"  {track.Kind} {track.Language} {track.Codec}{(track.Default ? " default" : "")}{(track.Forced ? " forced" : "")}" +
                            $"{(track.Commentary ? " commentary" : "")}{(track.HearingImpaired ? " SDH" : "")}{(track.VisualImpaired ? " AD" : "")}");
            }

            var values = MuxBuilder.NameValues(job, []);
            context.Log($"Then: the file goes to {JobPipeline.DoneFolder(context.Options.DoneFolder, job, values)}/ as " +
                        $"{JobPipeline.FileName(job, values)} (the specs in brackets come from the tracks), MKV title \"{JobPipeline.MkvTitle(job, values)}\".");
            context.Log("DVD subtitles (VobSub) go in as they are: no OCR to SRT for DVDs yet.");
            context.Log("Dry run: nothing was run.");
            return null;
        }

        // ---- Prepare: the MakeMKV rip ----
        context.Step(JobSteps.Prepare);
        var mkv = await RipTitleAsync(context, source, analysis, title, ripFolder);

        // The chapters as the rip has them: MakeMKV's times match the MKV's frames exactly.
        var chapters = await ChaptersAsync(context, mkv);
        if (chapters.Count == title.Mpls.Chapters.Count && chapters.Count > 0)
        {
            title = title.WithMpls(WithChapters(title.Mpls, chapters));
        }
        else if (title.Mpls.Chapters.Count > 0)
        {
            context.Warn($"The rip has {chapters.Count} chapters where the DVD lists {title.Mpls.Chapters.Count}: the cut and the chapters use the DVD's times.");
        }

        // ---- Demux: cut the episode out of the title ----
        context.Step(JobSteps.Demux);
        var input = mkv;
        if (part)
        {
            TryDelete(context.DemuxFolder);
            Directory.CreateDirectory(context.DemuxFolder);
            var start = title.Mpls.Chapters[playlist.FirstChapter - 1].Start;
            TimeSpan? end = playlist.LastChapter < title.Mpls.Chapters.Count ? title.Mpls.Chapters[playlist.LastChapter].Start : null;
            input = Path.Combine(context.DemuxFolder, "part.mkv");
            var cut = await context.RunAsync(new ProcessSpec(tools.MkvMerge, ["--gui-mode", ..CutArguments(input, mkv, start, end)]), Long,
                line => line.StartsWith("#GUI#progress", StringComparison.Ordinal) && MuxBuilder.PercentProgress(line) is { } p && Report(context, p),
                throwOnError: false);
            if (cut.ExitCode > 1 || !File.Exists(input))
            {
                throw new JobFailedException($"mkvmerge could not cut chapters {playlist.FirstChapter} to {playlist.LastChapter} out of the title: {cut.LastLine}");
            }

            playlist = DvdDisc.Part(title, playlist.FirstChapter, playlist.LastChapter) is { } cutPart
                ? new PlaylistInfo
                {
                    Name = playlist.Name, Mpls = cutPart.Mpls, Dvd = playlist.Dvd, PartOf = playlist.PartOf, FirstChapter = playlist.FirstChapter,
                    LastChapter = playlist.LastChapter, Size = playlist.Size
                }
                : playlist;
            context.Log($"Cut chapters {playlist.FirstChapter} to {playlist.LastChapter} ({Format.Duration(start)} to {(end is { } e ? Format.Duration(e) : "the end")}) into {input}.");
        }
        else
        {
            playlist = title;
            context.Log("The whole title: nothing to cut.");
        }

        // ---- Read streams ----
        context.Step(JobSteps.ReadStreams);
        var identify = await context.RunAsync(new ProcessSpec(tools.MkvMerge, ["-J", input]), TimeSpan.FromMinutes(5), _ => true);
        var tracks = ReadTracks(identify.Output);
        var info = await context.RunAsync(new ProcessSpec(tools.MediaInfo, ["--Output=JSON", input]), TimeSpan.FromMinutes(10), _ => true);
        var facts = MediaInfoReader.Parse(info.Output);
        foreach (var track in tracks)
        {
            context.Log($"  track {track.Id}: {track.Kind} {track.Codec} {track.Language ?? "und"}{(track.Forced ? " forced" : "")}{(track.Name is { Length: > 0 } n ? $" \"{n}\"" : "")}");
        }

        var (mapping, notes) = Map(playlist.Mpls, tracks, facts, input);
        foreach (var note in notes)
        {
            context.Log("  " + note);
        }

        foreach (var warning in mapping.Warnings)
        {
            context.Warn(warning);
        }

        var outputs = MuxBuilder.Outputs(job.Plan, mapping, job.Profile, job.OriginalLanguage);
        var missing = job.Plan.Kept.Where(t => !mapping.Files.ContainsKey(t.StreamIndex)).ToList();
        if (missing.Count > 0)
        {
            throw new JobFailedException($"The rip has no track for {missing.Count} kept stream(s): " +
                                         string.Join(", ", missing.Select(t => $"{t.Kind} {t.Language} {t.Codec}")) +
                                         ". Untick them on the job, or look at the tracks MakeMKV wrote in the log.");
        }

        if (outputs.Any(o => o.Planned.Kind == StreamKind.Subtitle))
        {
            context.Log("DVD subtitles (VobSub) go in as they are: no OCR to SRT for DVDs yet.");
        }

        return (playlist, outputs);
    }

    /// <summary>After a finished job: the title's rip goes once no other job of the queue needs it.</summary>
    public static void CleanUp(JobContext context)
    {
        var job = context.Job;
        if (!job.Dvd || DvdDisc.TitleNumber(job.Playlist) == null)
        {
            return;
        }

        var title = DvdDisc.ParsePart(job.Playlist)?.Title ?? job.Playlist;
        var others = context.Store.List(500, [JobStatus.Queued, JobStatus.Running, JobStatus.Waiting, JobStatus.Failed, JobStatus.Interrupted])
            .Where(j => j.Id != job.Id && j.Dvd && j.SourcePath == job.SourcePath && !j.Dismissed && !j.DryRun
                        && (DvdDisc.ParsePart(j.Playlist)?.Title ?? j.Playlist) == title)
            .ToList();
        var folder = RipFolder(context.Options.WorkFolder, job.SourcePath, title);
        if (others.Count > 0)
        {
            context.Log($"Kept the title's rip {folder}: {others.Count} other job(s) of this title still need it.");
            return;
        }

        JobPipeline.TryDeleteFolder(context, folder);
    }

    internal static List<string> CutArguments(string output, string input, TimeSpan start, TimeSpan? end) =>
        ["-o", output, "--no-chapters", "--split", $"parts:{Stamp(start)}-{(end is { } e ? Stamp(e) : "")}", input];

    private static string Stamp(TimeSpan time) => time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    /// <summary>The rip of a title already made (by another episode's job), when it is complete and from this version of the disc.</summary>
    private static string? Ripped(string folder, DiscSource source)
    {
        var mkv = Path.Combine(folder, "title.mkv");
        var done = Path.Combine(folder, "done");
        try
        {
            return File.Exists(mkv) && File.Exists(done) && File.ReadAllText(done).Trim() == Stamp(source) ? mkv : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Stamp(DiscSource source) => $"{source.Path}|{source.Modified.Ticks.ToString(CultureInfo.InvariantCulture)}";

    private static async Task<string> RipTitleAsync(JobContext context, DiscSource source, DiscAnalysis analysis, PlaylistInfo title, string folder)
    {
        var rip = Rips.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
        await rip.WaitAsync(context.CancellationToken);
        try
        {
            if (Ripped(folder, source) is { } done)
            {
                context.Log($"The title is ripped already (for another episode of it): {done}.");
                return done;
            }

            TryDelete(folder);
            Directory.CreateDirectory(folder);
            var home = Path.GetFullPath(context.Options.MakeMkvHome);
            Directory.CreateDirectory(home);
            // Every track: the job's plan picks them, not MakeMKV's language rules.
            MakeMkv.WriteSetting(home, "app_DefaultSelectionString", MakeMkv.SelectAll);
            var environment = new Dictionary<string, string> { ["HOME"] = home };

            var info = await context.RunAsync(new ProcessSpec(context.Options.Tools.MakeMkvCon,
                ["-r", "--noscan", "--minlength=0", "info", MakeMkv.SourceArgument(source)]) { Environment = environment }, TimeSpan.FromMinutes(20),
                line => MakeMkv.ParseRobot(line) is { Type: not "MSG" }, throwOnError: false);
            if (info.NotStarted)
            {
                throw new JobFailedException("makemkvcon is not installed.");
            }

            var titles = MakeMkv.Titles(info.Output);
            var all = analysis.Playlists.Where(p => p.Dvd != null && p.PartOf == null).Select(p => p.Dvd!).ToList();
            var match = MakeMkv.DvdTitle(titles, title.Dvd!, all)
                        ?? throw new JobFailedException($"MakeMKV lists no title of {Format.Duration(title.Duration)} (DVD title {title.Dvd!.Number}): " +
                                                        (titles.Count == 0
                                                            ? MakeMkv.KeyMessages(info.Output).FirstOrDefault() ?? "it listed no titles at all; see its messages in the log."
                                                            : "it lists " + string.Join(", ", titles.Select(t => $"#{t.Id} {Format.Duration(t.Duration)}")) + "."));
            context.Log($"MakeMKV's title #{match.Id} is DVD title {title.Dvd!.Number} ({Format.Duration(match.Duration)}, {match.Chapters} chapters" +
                        (match.OriginalTitle is { } original ? $", original title {original}" : "") + ").");

            var failed = false;
            var result = await context.RunAsync(new ProcessSpec(context.Options.Tools.MakeMkvCon,
                ["-r", "--progress=-same", "--noscan", "--minlength=0", "mkv", MakeMkv.SourceArgument(source), match.Id.ToString(CultureInfo.InvariantCulture), folder])
            {
                Environment = environment
            }, Long, line =>
            {
                var robot = MakeMkv.ParseRobot(line);
                if (robot == null)
                {
                    return false;
                }

                if (MakeMkv.Progress(robot) is { } p)
                {
                    context.Progress(p);
                }
                else if (robot.Message is { } message)
                {
                    context.Log("  MakeMKV: " + message);
                    failed |= message.Contains("failed", StringComparison.OrdinalIgnoreCase) && !message.Contains("0 failed", StringComparison.OrdinalIgnoreCase)
                              || message.Contains("too old", StringComparison.OrdinalIgnoreCase);
                }

                return true;
            }, throwOnError: false);

            var written = Directory.EnumerateFiles(folder, "*.mkv").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (!result.Success || failed || written == null)
            {
                throw new JobFailedException("MakeMKV could not rip the title: see its messages in the log" +
                                             (result.NotStarted ? " (makemkvcon is not installed)." : "."));
            }

            var mkv = Path.Combine(folder, "title.mkv");
            File.Move(written, mkv, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(folder, "done"), Stamp(source), context.CancellationToken);
            context.Log($"Ripped DVD title {title.Dvd.Number} to {mkv} ({Format.Size(new FileInfo(mkv).Length)}).");
            return mkv;
        }
        finally
        {
            rip.Release();
        }
    }

    /// <summary>The chapter starts of an MKV, from ffprobe.</summary>
    private static async Task<List<TimeSpan>> ChaptersAsync(JobContext context, string mkv)
    {
        var result = await ProcessRunner.RunAsync(context.Options.Tools.FFprobe,
            ["-v", "error", "-show_chapters", "-of", "json", mkv], TimeSpan.FromMinutes(2), cancellationToken: context.CancellationToken);
        return ParseChapters(result.Output);
    }

    internal static List<TimeSpan> ParseChapters(string json)
    {
        var starts = new List<TimeSpan>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("chapters", out var chapters))
            {
                foreach (var chapter in chapters.EnumerateArray())
                {
                    if (chapter.TryGetProperty("start_time", out var start)
                        && double.TryParse(start.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        starts.Add(TimeSpan.FromMilliseconds(Math.Round(seconds * 1000)));
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return starts;
    }

    private static Mpls WithChapters(Mpls mpls, IReadOnlyList<TimeSpan> chapters) => new()
    {
        Version = mpls.Version, Items = mpls.Items, Streams = mpls.Streams, SubPaths = mpls.SubPaths,
        Chapters = chapters.Select(c => new ChapterMark(c)).ToList()
    };

    /// <summary>The tracks of mkvmerge -J's output.</summary>
    internal static List<MkvInputTrack> ReadTracks(string json)
    {
        var tracks = new List<MkvInputTrack>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tracks", out var list))
            {
                return tracks;
            }

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
                string? Text(string name) => properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
                bool Flag(string name) => properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                var number = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("number", out var n) && n.TryGetInt32(out var value) ? value : 0;
                var language = Text("language");
                tracks.Add(new MkvInputTrack(track.GetProperty("id").GetInt32(), number, kind, track.TryGetProperty("codec", out var c) ? c.GetString() ?? "" : "",
                    string.IsNullOrEmpty(language) || language == "und" ? null : language, Flag("forced_track"), Text("track_name")));
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return tracks;
    }

    /// <summary>
    /// Ties each DVD stream to a track of the rip: in order within each kind, by language. A
    /// track whose language matches none of the next streams is one MakeMKV added (the forced
    /// captions it finds in a subtitle stream get a track of their own): it is left out. A
    /// stream with no track left is reported.
    /// </summary>
    internal static (StreamMapping Mapping, List<string> Notes) Map(Mpls playlist, IReadOnlyList<MkvInputTrack> tracks, IReadOnlyList<StreamFacts> facts, string path)
    {
        var mapping = new StreamMapping();
        var notes = new List<string>();
        foreach (var kind in new[] { StreamKind.Video, StreamKind.Audio, StreamKind.Subtitle })
        {
            var streams = playlist.Streams.Select((s, i) => (Stream: s, Index: i)).Where(x => x.Stream.Kind == kind).ToList();
            var pool = tracks.Where(t => t.Kind == kind).ToList();
            var kindFacts = facts.Where(f => f.Kind == kind).ToList();
            var next = 0;
            foreach (var (stream, index) in streams)
            {
                // The next track in this stream's language (or with none), skipping MakeMKV's extra forced-only ones.
                var at = -1;
                for (var j = next; j < pool.Count; j++)
                {
                    var same = kind == StreamKind.Video || pool[j].Language == null || stream.Language == null || Languages.Same(pool[j].Language, stream.Language);
                    if (same && !(pool[j].Forced && !stream.Forced && kind == StreamKind.Subtitle && j + 1 < pool.Count && Languages.Same(pool[j + 1].Language, stream.Language)))
                    {
                        at = j;
                        break;
                    }

                    // A language that comes later on the DVD: this stream has no track.
                    if (streams.Any(s => s.Index > index && Languages.Same(s.Stream.Language, pool[j].Language)) && !pool[j].Forced)
                    {
                        break;
                    }
                }

                if (at < 0)
                {
                    mapping.Warnings.Add($"The rip has no track for the DVD's {Plural(kind)} stream 0x{stream.Pid:X2} ({Languages.Name(stream.Language)} {stream.CodecName}).");
                    continue;
                }

                for (var skipped = next; skipped < at; skipped++)
                {
                    notes.Add($"Track {pool[skipped].Id} ({pool[skipped].Codec} {pool[skipped].Language ?? "und"}{(pool[skipped].Forced ? ", forced captions only" : "")}) is not one of the DVD's streams: left out.");
                }

                var track = pool[at];
                var fact = kindFacts.FirstOrDefault(f => f.Id == track.Number && track.Number > 0)
                           ?? (pool.IndexOf(track) < kindFacts.Count ? kindFacts[pool.IndexOf(track)] : null)
                           ?? new StreamFacts { Kind = kind, Format = track.Codec };
                if (kind == StreamKind.Subtitle && string.IsNullOrEmpty(fact.Format))
                {
                    fact = fact with { Format = "VobSub" };
                }

                mapping.Files[index] = new DemuxedFile(path, fact, track.Id);
                next = at + 1;
            }

            for (var left = next; left < pool.Count; left++)
            {
                notes.Add($"Track {pool[left].Id} ({pool[left].Codec} {pool[left].Language ?? "und"}{(pool[left].Forced ? ", forced captions only" : "")}) is not one of the DVD's streams: left out.");
            }
        }

        return (mapping, notes);
    }

    private static void CheckSpace(JobContext context, long rip, long part, bool cut)
    {
        // The rip (unless there already), the cut episode, the finished MKV.
        var need = (long)(rip * 1.05) + (long)(part * (cut ? 2.2 : 1.1));
        var spare = (long)(context.Options.SpareSpaceGb * 1e9);
        try
        {
            var free = new DriveInfo(context.Options.WorkFolder).AvailableFreeSpace;
            context.Log($"Space: needs about {Format.Size(need)} + {Format.Size(spare)} spare, {Format.Size(free)} free in {context.Options.WorkFolder}.");
            if (free < need + spare)
            {
                throw new JobFailedException($"Not enough free space in {context.Options.WorkFolder}: about {Format.Size(need)} needed, {Format.Size(free)} free.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            context.Warn($"Could not read the free space of {context.Options.WorkFolder}: {e.Message}");
        }
    }

    private static bool Report(JobContext context, double progress)
    {
        context.Progress(progress);
        return true;
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

    private static string Plural(StreamKind kind) => kind switch
    {
        StreamKind.Video => "video",
        StreamKind.Audio => "audio",
        _ => "subtitle"
    };
}
