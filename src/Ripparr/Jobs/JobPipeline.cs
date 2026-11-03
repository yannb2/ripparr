using System.Globalization;
using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Options;
using Ripparr.Profiles;
using Ripparr.Tools;

namespace Ripparr.Jobs;

/// <summary>
/// The steps of one job: check, prepare (decrypt or unpack), demux, read the streams,
/// merge the Dolby Vision layer, build the MKV, verify, move it to the done folder.
/// </summary>
public sealed class JobPipeline(DiscAnalyzer analyzer)
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(12);

    public async Task RunAsync(JobContext context)
    {
        var job = context.Job;
        var tools = context.Options.Tools;

        // ---- Check ----
        context.Step(JobSteps.Check);
        var folderProblems = new[] { ("work folder", context.Options.WorkFolder), ("done folder", context.Options.DoneFolder) }
            .Select(f => FolderCheck.Check(f.Item2) is { } error ? new FolderProblem(f.Item1, f.Item2, error) : null)
            .OfType<FolderProblem>().ToList();
        if (folderProblems.Count > 0)
        {
            throw new JobFailedException(
                $"Cannot write to the {string.Join(" and ", folderProblems.Select(p => $"{p.Name} {p.Path}"))}. " +
                $"Give it to the container's user on the host: {FolderCheck.Hint(folderProblems)}");
        }

        var source = analyzer.Describe(job.SourcePath)
                     ?? throw new JobFailedException($"The source is gone: {job.SourcePath}");
        var analysis = await analyzer.AnalyseAsync(source, context.CancellationToken, fresh: true);
        if (analysis.Error != null)
        {
            throw new JobFailedException(analysis.Error);
        }

        var playlist = analysis.Find(job.Playlist)
                       ?? throw new JobFailedException($"Playlist {job.Playlist} is not on this disc.");
        context.Log($"Source: {source.Path} ({source.Kind}, {Format.Size(source.Size)})");
        context.Log($"Playlist {playlist.Name}: {Format.Duration(playlist.Duration)}, {playlist.Mpls.Chapters.Count} chapters, " +
                    $"{playlist.Mpls.Clips.Count} clip(s), {Format.Size(playlist.Size)}" +
                    (playlist.Mpls.HasDolbyVisionEnhancementLayer ? ", Dolby Vision enhancement layer" : ""));

        var prepare = PrepareMode(source, analysis);
        context.Log($"Encryption: {analysis.Encryption}. Prepare: {Describe(prepare)}.");
        Directory.CreateDirectory(context.WorkFolder);
        CheckSpace(context, source, analysis, playlist, prepare);

        if (job.DryRun)
        {
            DryRun(context, source, playlist, prepare);
            return;
        }

        // ---- Prepare ----
        context.Step(JobSteps.Prepare);
        var discFolder = await PrepareAsync(context, source, playlist, prepare);
        var mplsPath = DiscAnalyzer.PlaylistPath(discFolder, playlist.Name)
                       ?? throw new JobFailedException($"Playlist {playlist.Name}.mpls is missing after the prepare step.");
        var firstClip = DiscAnalyzer.ClipPath(discFolder, playlist.Mpls.Clips[0]);
        if (firstClip != null && EncryptionCheck.Check(firstClip) == Encryption.Encrypted)
        {
            throw new JobFailedException("The streams are still encrypted. MakeMKV could not decrypt them: check its key and messages on the Tools page.");
        }

        // ---- Demux ----
        context.Step(JobSteps.Demux);
        Directory.CreateDirectory(context.DemuxFolder);
        var prefix = Path.Combine(context.DemuxFolder, playlist.Name);
        var demuxArguments = ExpandArguments(context.Options.DgDemuxArguments, mplsPath, prefix);
        await context.RunAsync(new ProcessSpec(tools.DgDemux, demuxArguments) { WorkingDirectory = context.DemuxFolder }, Long,
            line =>
            {
                if (MuxBuilder.PercentProgress(line) is { } p && line.Length < 40)
                {
                    context.Progress(p);
                    return true;
                }

                return false;
            });

        // ---- Read streams ----
        context.Step(JobSteps.ReadStreams);
        var files = new List<DemuxedFile>();
        foreach (var path in Directory.EnumerateFiles(context.DemuxFolder).Order(StringComparer.Ordinal))
        {
            if (!MuxBuilder.IsStreamFile(path))
            {
                context.Log($"  skipped {Path.GetFileName(path)}");
                continue;
            }

            var info = await context.RunAsync(new ProcessSpec(tools.MediaInfo, ["--Output=JSON", path]), TimeSpan.FromMinutes(10),
                _ => true);
            var facts = MediaInfoReader.Parse(info.Output).FirstOrDefault();
            if (facts == null)
            {
                context.Warn($"mediainfo could not read {Path.GetFileName(path)}; left out.");
                continue;
            }

            files.Add(new DemuxedFile(path, facts));
            context.Log($"  {Path.GetFileName(path)}: {Describe(facts)}");
        }

        var mapping = MuxBuilder.Map(playlist.Mpls, files);
        foreach (var warning in mapping.Warnings)
        {
            context.Warn(warning);
        }

        var outputs = MuxBuilder.Outputs(job.Plan, mapping, job.Profile);
        job.Written = outputs.Select(o => new WrittenTrack(
            o.Planned.Kind.ToString(),
            o.Planned.Kind switch
            {
                StreamKind.Audio => $"{o.File.Facts.AudioCodec} {o.File.Facts.ChannelWords}".Trim(),
                StreamKind.Video => $"{o.File.Facts.VideoCodec} {o.File.Facts.Resolution} {o.File.Facts.DynamicRange}".Trim(),
                _ => o.Planned.Codec
            },
            o.Planned.Language,
            o.Name,
            string.Join(' ', new[] { o.Planned.Default ? "default" : "", o.Planned.Forced ? "forced" : "",
                                     o.Planned.Commentary ? "commentary" : "" }.Where(w => w != "")))).ToList();
        foreach (var output in outputs)
        {
            context.Log($"  track: {output.Planned.Kind} {output.Planned.Language} \"{output.Name}\" <- {output.File.FileName}");
        }

        var missing = job.Plan.Kept.Where(t => !mapping.Files.ContainsKey(t.StreamIndex)).ToList();
        if (missing.Count > 0)
        {
            throw new JobFailedException($"No demuxed file for {missing.Count} kept track(s): " +
                                         string.Join(", ", missing.Select(t => $"{t.Kind} {t.Language} {t.Codec}")));
        }

        // ---- Dolby Vision ----
        string? videoPath = null;
        var video = outputs.FirstOrDefault(o => o.Planned.Kind == StreamKind.Video);
        if (job.Plan.MergeDolbyVisionLayer)
        {
            context.Step(JobSteps.DolbyVision);
            if (video?.File.Facts.DualLayer == true)
            {
                context.Log("The video already carries its enhancement layer (BL+EL+RPU): nothing to merge.");
            }
            else if (mapping.EnhancementLayer == null || video == null)
            {
                throw new JobFailedException("The plan keeps the Dolby Vision layer but DGDemux wrote no enhancement layer file. " +
                                             "Untick the layer on the job to keep only the HDR10 base.");
            }
            else
            {
                videoPath = Path.Combine(context.DemuxFolder, $"{playlist.Name}.bl-el.hevc");
                await context.RunAsync(new ProcessSpec(tools.DoviTool,
                    ["mux", "--bl", video.File.Path, "--el", mapping.EnhancementLayer.Path, "-o", videoPath]), Long,
                    line => MuxBuilder.PercentProgress(line) is { } p && Report(context, p));
                context.Log($"Merged {mapping.EnhancementLayer.FileName} into {Path.GetFileName(videoPath)}.");
            }
        }

        // ---- Build the MKV ----
        context.Step(JobSteps.Mux);
        Directory.CreateDirectory(context.OutFolder);
        var values = MuxBuilder.NameValues(job, outputs);
        var fileName = Naming.SafeFileName(Naming.Apply(job.Profile.FileNameTemplate, values)) + ".mkv";
        var title = Naming.Apply(job.Profile.TitleTemplate, values);
        var outPath = Path.Combine(context.OutFolder, fileName);

        string? chaptersPath = null;
        if (playlist.Mpls.Chapters.Count > 0)
        {
            chaptersPath = Path.Combine(context.WorkFolder, "chapters.txt");
            await File.WriteAllTextAsync(chaptersPath, MuxBuilder.ChapterFile(playlist.Mpls, job.Profile.Chapters), context.CancellationToken);
        }

        string? tagsPath = null;
        if (job.Profile.WriteTags)
        {
            tagsPath = Path.Combine(context.WorkFolder, "tags.xml");
            await File.WriteAllTextAsync(tagsPath, MuxBuilder.TagsXml(job, outputs), context.CancellationToken);
        }

        var frameRate = playlist.Mpls.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video && !s.DolbyVisionEnhancementLayer) is { } v
            ? StreamCoding.FrameRate(v.Rate)
            : null;
        var mkvmerge = MuxBuilder.MkvmergeArguments(outPath, title, outputs, chaptersPath, tagsPath, frameRate, videoPath);
        var muxResult = await context.RunAsync(new ProcessSpec(tools.MkvMerge, mkvmerge), Long, line =>
        {
            if (line.StartsWith("#GUI#progress", StringComparison.Ordinal) && MuxBuilder.PercentProgress(line) is { } p)
            {
                context.Progress(p);
                return true;
            }

            return false;
        }, throwOnError: false);

        // mkvmerge exits 1 for warnings: the file is written.
        if (muxResult.ExitCode > 1 || !File.Exists(outPath))
        {
            throw new JobFailedException($"mkvmerge failed (exit code {muxResult.ExitCode}): {muxResult.LastLine}");
        }

        if (muxResult.ExitCode == 1)
        {
            context.Warn("mkvmerge finished with warnings (see the log).");
        }

        // ---- Verify ----
        context.Step(JobSteps.Verify);
        job.Checks = await Verifier.VerifyAsync(context, outPath, playlist, outputs, job.Plan.MergeDolbyVisionLayer);
        foreach (var check in job.Checks)
        {
            context.Log($"  {(check.Passed ? check.Warning ? "WARN" : "OK  " : "FAIL")} {check.Name}: {check.Detail}");
        }

        context.Save();
        if (job.Checks.Any(c => !c.Passed))
        {
            throw new JobFailedException("Verify failed: " + string.Join("; ", job.Checks.Where(c => !c.Passed).Select(c => c.Name)) +
                                         ". The file stays in the job folder.");
        }

        // ---- Finish ----
        context.Step(JobSteps.Finish);
        var folder = Path.Combine(context.Options.DoneFolder,
            Naming.SafeFileName(Naming.Apply("{title} ({year})", values)));
        Directory.CreateDirectory(folder);
        var target = FreeName(Path.Combine(folder, fileName));
        File.Move(outPath, target);
        job.OutputPath = target;
        context.Log($"Done: {target}");
        if (context.Options.CleanWorkFolder)
        {
            TryDeleteFolder(context, context.WorkFolder);
        }
    }

    public enum Prepare
    {
        /// <summary>A plain BDMV folder: read where it is.</summary>
        InPlace,

        /// <summary>A plain ISO: unpack the playlist's clips with 7-Zip.</summary>
        Unpack,

        /// <summary>Encrypted: MakeMKV backup --decrypt into the job folder.</summary>
        Decrypt
    }

    public static Prepare PrepareMode(DiscSource source, DiscAnalysis analysis)
    {
        if (analysis.Encryption is Encryption.Encrypted or Encryption.Likely)
        {
            return Prepare.Decrypt;
        }

        return source.Kind == SourceKind.Iso ? Prepare.Unpack : Prepare.InPlace;
    }

    private static string Describe(Prepare prepare) => prepare switch
    {
        Prepare.InPlace => "read the folder where it is",
        Prepare.Unpack => "unpack the playlist's clips with 7-Zip",
        _ => "decrypt a copy with MakeMKV"
    };

    private static string Describe(StreamFacts facts) => facts.Kind switch
    {
        StreamKind.Video => $"{facts.VideoCodec} {facts.Width}x{facts.Height} {facts.FrameRate} {facts.DynamicRange} {facts.HdrSettings}".Trim(),
        StreamKind.Audio => $"{facts.AudioCodec} {facts.ChannelWords}",
        _ => facts.Format ?? "subtitle"
    };

    private async Task<string> PrepareAsync(JobContext context, DiscSource source, PlaylistInfo playlist, Prepare prepare)
    {
        var tools = context.Options.Tools;
        switch (prepare)
        {
            case Prepare.InPlace:
                context.Log($"Reading {source.Path} in place.");
                return source.Path;

            case Prepare.Unpack:
            {
                var reader = new IsoReader(tools.SevenZip);
                List<string> patterns = ["BDMV/index.bdmv", "BDMV/MovieObject.bdmv", "BDMV/PLAYLIST/*", "BDMV/CLIPINF/*"];
                patterns.AddRange(playlist.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase).Select(c => $"BDMV/STREAM/{c}.m2ts"));
                context.Log("$ " + new ProcessSpec(tools.SevenZip, ["x", source.Path, $"-o{context.DiscFolder}", "-y", "-bsp1", ..patterns]).CommandLine);
                var result = await reader.ExtractAsync(source.Path, context.DiscFolder, patterns, context.CancellationToken,
                    line =>
                    {
                        if (MuxBuilder.PercentProgress(line) is { } p && line.Trim().Length < 60)
                        {
                            context.Progress(p);
                        }
                    }, Long);
                if (result.Success && DiscAnalyzer.PlaylistPath(context.DiscFolder, playlist.Name) != null)
                {
                    return context.DiscFolder;
                }

                context.Warn($"7-Zip could not unpack the ISO ({result.LastLine}); trying MakeMKV backup instead.");
                TryDeleteFolder(context, context.DiscFolder);
                return await BackupAsync(context, source, decrypt: false);
            }

            default:
                return await BackupAsync(context, source, decrypt: true);
        }
    }

    private static async Task<string> BackupAsync(JobContext context, DiscSource source, bool decrypt)
    {
        Directory.CreateDirectory(context.DiscFolder);
        List<string> arguments = ["-r", "--progress=-same", "--noscan", "backup"];
        if (decrypt)
        {
            arguments.Add("--decrypt");
        }

        arguments.AddRange([MakeMkv.SourceArgument(source), context.DiscFolder]);
        Directory.CreateDirectory(context.Options.MakeMkvHome);
        var spec = new ProcessSpec(context.Options.Tools.MakeMkvCon, arguments)
        {
            Environment = new Dictionary<string, string> { ["HOME"] = Path.GetFullPath(context.Options.MakeMkvHome) }
        };

        var failed = false;
        var result = await context.RunAsync(spec, Long, line =>
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
                failed |= message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                          || message.Contains("too old", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }, throwOnError: false);

        if (!result.Success || failed || SourceFinder.BdmvParent(context.DiscFolder) == null)
        {
            throw new JobFailedException("MakeMKV backup failed: " +
                                         (result.NotStarted ? "makemkvcon is not installed." : "see its messages in the log and the key on the Tools page."));
        }

        return context.DiscFolder;
    }

    private static void CheckSpace(JobContext context, DiscSource source, DiscAnalysis analysis, PlaylistInfo playlist, Prepare prepare)
    {
        var discSize = source.Kind == SourceKind.Iso ? source.Size : analysis.ClipSizes.Values.Sum();
        long need = prepare switch
        {
            Prepare.Decrypt => discSize,
            Prepare.Unpack => playlist.Size,
            _ => 0
        };

        // Demuxed streams, the merged video, the MKV.
        need += (long)(playlist.Size * 2.5);
        var spare = (long)(context.Options.SpareSpaceGb * 1e9);
        try
        {
            var free = new DriveInfo(context.WorkFolder).AvailableFreeSpace;
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

    private static void DryRun(JobContext context, DiscSource source, PlaylistInfo playlist, Prepare prepare)
    {
        var tools = context.Options.Tools;
        context.Step("Dry run");
        var disc = prepare == Prepare.InPlace ? source.Path : context.DiscFolder;
        if (prepare == Prepare.Decrypt)
        {
            context.Log("$ " + new ProcessSpec(tools.MakeMkvCon, ["-r", "--progress=-same", "--noscan", "backup", "--decrypt",
                MakeMkv.SourceArgument(source), context.DiscFolder]).CommandLine);
        }
        else if (prepare == Prepare.Unpack)
        {
            context.Log("$ " + new ProcessSpec(tools.SevenZip, ["x", source.Path, $"-o{context.DiscFolder}", "-y", "BDMV/PLAYLIST/*",
                "BDMV/CLIPINF/*", ..playlist.Mpls.Clips.Distinct().Select(c => $"BDMV/STREAM/{c}.m2ts")]).CommandLine);
        }

        var mpls = Path.Combine(disc, "BDMV", "PLAYLIST", playlist.Name + ".mpls");
        context.Log("$ " + new ProcessSpec(tools.DgDemux,
            ExpandArguments(context.Options.DgDemuxArguments, mpls, Path.Combine(context.DemuxFolder, playlist.Name))).CommandLine);
        context.Log("Then: mediainfo on each stream, dovi_tool mux for the Dolby Vision layer, mkvmerge with these tracks:");
        foreach (var track in context.Job.Plan.Kept)
        {
            context.Log($"  {track.Kind} {track.Language} {track.Codec}{(track.Default ? " default" : "")}{(track.Forced ? " forced" : "")}" +
                        $"{(track.Commentary ? " commentary" : "")}{(string.IsNullOrWhiteSpace(track.Name) ? "" : $" \"{track.Name}\"")}");
        }

        if (context.Job.Plan.MergeDolbyVisionLayer)
        {
            context.Log("  + the Dolby Vision enhancement layer, merged into the video");
        }

        context.Log("Dry run: nothing was run.");
    }

    /// <summary>Splits the DGDemux argument setting on spaces and fills {input} and {output}.</summary>
    public static List<string> ExpandArguments(string template, string input, string output) =>
        template.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Replace("{input}", input).Replace("{output}", output))
            .ToList();

    private static bool Report(JobContext context, double progress)
    {
        context.Progress(progress);
        return true;
    }

    private static string FreeName(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static void TryDeleteFolder(JobContext context, string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                context.Log($"Deleted {folder}.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Warn($"Could not delete {folder}: {e.Message}");
        }
    }
}
