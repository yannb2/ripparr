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

        var prepare = PrepareMode(source, analysis, context.Options.IsoMount.Enabled && !job.UnpackIso);
        context.Log($"Encryption: {analysis.Encryption}. Prepare: {Describe(prepare)}.");
        job.StepWeights = JobProgress.Weights(prepare, job.Plan.MergeDolbyVisionLayer,
            job.Plan.Kept.Count(t => SubtitleStep.WantsSrt(job.Profile, t, job.OriginalLanguage)));
        Directory.CreateDirectory(context.WorkFolder);
        CheckSpace(context, source, analysis, playlist, prepare);

        if (job.DryRun)
        {
            await DryRunAsync(context, source, playlist, prepare);
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
        var demuxArguments = DgDemuxCommand.Arguments(job.Profile, context.Options.DgDemuxArguments, mplsPath, prefix,
            DgDemuxCommand.DemuxedPids(job.Profile, job.Plan));
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
            if (facts?.Kind != StreamKind.Subtitle && MuxBuilder.IsSubtitleFile(path))
            {
                // MediaInfo sees nothing in a bare .sup (only the General part): read the captions ourselves.
                facts = MuxBuilder.SupFacts(path) ?? facts;
            }

            if (facts == null)
            {
                context.Warn($"mediainfo could not read {Path.GetFileName(path)}; left out.");
                continue;
            }

            files.Add(new DemuxedFile(path, facts));
            context.Log($"  {Path.GetFileName(path)}: {Describe(facts)}");
        }

        var mapping = MuxBuilder.Map(playlist.Mpls, files, DgDemuxCommand.DemuxedPids(job.Profile, job.Plan));
        foreach (var warning in mapping.Warnings)
        {
            context.Warn(warning);
        }

        var outputs = MuxBuilder.Outputs(job.Plan, mapping, job.Profile, job.OriginalLanguage);
        var missing = job.Plan.Kept.Where(t => !mapping.Files.ContainsKey(t.StreamIndex)).ToList();
        if (missing.Count > 0)
        {
            throw new JobFailedException($"No demuxed file for {missing.Count} kept track(s): " +
                                         string.Join(", ", missing.Select(t => $"{t.Kind} {t.Language} {t.Codec}")));
        }

        // ---- Subtitles ----
        context.Step(JobSteps.Subtitles);
        outputs = await SubtitleStep.RunAsync(context, outputs);
        context.Save();

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

        // The video's name, now that the Dolby Vision profile and layer can be read from the merged stream.
        if (video != null && string.IsNullOrWhiteSpace(video.Planned.Name) && (videoPath != null || video.File.Facts.DolbyVision))
        {
            DoviTool.Executable = tools.DoviTool;
            var read = await DoviTool.ReadAsync(videoPath ?? video.File.Path, DoviTool.SampleFrames, TimeSpan.FromMinutes(2), context.CancellationToken);
            if (read.Details is { } dv)
            {
                var name = MuxBuilder.TrackName(video.Planned, video.File.Facts, job.Profile, job.OriginalLanguage, dv.Profile ?? "7", dv.Layer);
                outputs[outputs.IndexOf(video)] = video with { Name = name };
                context.Log($"Dolby Vision profile {dv.Profile} {dv.Layer}: the video track is \"{name}\".");
            }
            else
            {
                context.Warn($"Could not read the Dolby Vision profile for the video's name: {read.Error}");
            }
        }

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
                                     o.Planned.Commentary ? "commentary" : "",
                                     o.Planned.HearingImpaired ? "SDH" : "", o.Planned.VisualImpaired ? "AD" : "" }.Where(w => w != "")),
            o.File.Facts.Details,
            FileSize(o.File.Path, o.File.Facts),
            o.File.Facts.BitRateFor(FileSize(o.File.Path, o.File.Facts)),
            o.File.Facts.BitRateMaximum,
            o.File.FileName)).ToList();
        foreach (var output in outputs)
        {
            context.Log($"  track: {output.Planned.Kind} {output.Planned.Language} \"{output.Name}\" <- {output.File.FileName}");
        }

        // ---- Build the MKV ----
        context.Step(JobSteps.Mux);
        Directory.CreateDirectory(context.OutFolder);
        var values = MuxBuilder.NameValues(job, outputs);
        // An extra is named by hand ("Making Of"): Jellyfin shows the file name.
        var fileName = FileName(job, values);
        var title = MkvTitle(job, values);
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
            if (job.Tags.Any(t => t.Value.Length > 0))
            {
                context.Log("Your global tags: " + string.Join(", ", job.Tags.Where(t => t.Value.Length > 0).Select(t => $"{t.Name}={t.Value}")));
            }
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
        var folder = DoneFolder(context.Options.DoneFolder, job, values);
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

    /// <summary>
    /// done/Title (Year)/; for an extra done/Title (Year)/featurettes/ (its type's folder); for an
    /// episode done/Series (Year)/Season 01/, as Sonarr lays them out (a TV extra can go in a season
    /// folder too: Season 01/featurettes/).
    /// </summary>
    public static string DoneFolder(string doneRoot, Job job, IReadOnlyDictionary<string, string?> values)
    {
        var film = Path.Combine(doneRoot, Naming.SafeFileName(Naming.Apply("{title} ({year})", values)));
        // An episode, or a TV extra put with its season.
        if (job.Season is { } season)
        {
            film = Path.Combine(film, EpisodeNumbers.SeasonFolder(season));
        }

        return job.IsExtra ? Path.Combine(film, job.ExtraType!) : film;
    }

    /// <summary>The MKV's file name: an extra's own name, an episode's or the film's template.</summary>
    public static string FileName(Job job, IReadOnlyDictionary<string, string?> values) =>
        Naming.SafeFileName(job.IsExtra ? job.ExtraName!
            : Naming.Apply(job.IsEpisode ? job.Profile.EpisodeFileNameTemplate : job.Profile.FileNameTemplate, values)) + ".mkv";

    /// <summary>The MKV title.</summary>
    public static string MkvTitle(Job job, IReadOnlyDictionary<string, string?> values) =>
        job.IsExtra ? job.ExtraName!.Trim()
            : Naming.Apply(job.IsEpisode ? job.Profile.EpisodeTitleTemplate : job.Profile.TitleTemplate, values);

    public enum Prepare
    {
        /// <summary>A plain BDMV folder: read where it is.</summary>
        InPlace,

        /// <summary>A plain ISO: unpack the playlist's clips with 7-Zip.</summary>
        Unpack,

        /// <summary>A plain ISO: mounted read-only by the mount service on the host, then read in place.</summary>
        Mount,

        /// <summary>Encrypted: MakeMKV backup --decrypt into the job folder.</summary>
        Decrypt
    }

    /// <param name="mount">Mount plain ISOs (the mount service is on and the job was not told to unpack).</param>
    public static Prepare PrepareMode(DiscSource source, DiscAnalysis analysis, bool mount)
    {
        if (analysis.Encryption is Encryption.Encrypted or Encryption.Likely)
        {
            return Prepare.Decrypt;
        }

        return source.Kind != SourceKind.Iso ? Prepare.InPlace : mount ? Prepare.Mount : Prepare.Unpack;
    }

    private static string Describe(Prepare prepare) => prepare switch
    {
        Prepare.InPlace => "read the folder where it is",
        Prepare.Unpack => "unpack the playlist's clips with 7-Zip",
        Prepare.Mount => "mount the ISO read-only and read it in place",
        _ => "decrypt a copy with MakeMKV"
    };

    private static string Describe(StreamFacts facts) => facts.Kind switch
    {
        StreamKind.Video => $"{facts.VideoCodec} {facts.Width}x{facts.Height} {facts.FrameRate} {facts.DynamicRange} {facts.HdrSettings}".Trim(),
        StreamKind.Audio => $"{facts.AudioCodec} {facts.ChannelWords}",
        _ => facts.Format ?? "subtitle"
    };

    private static async Task<string> PrepareAsync(JobContext context, DiscSource source, PlaylistInfo playlist, Prepare prepare)
    {
        switch (prepare)
        {
            case Prepare.InPlace:
                context.Log($"Reading {source.Path} in place.");
                return source.Path;

            case Prepare.Mount:
            {
                var mounted = await MountAsync(context, source, playlist);
                if (mounted != null)
                {
                    return mounted;
                }

                context.Job.StepWeights[JobSteps.Prepare] = JobProgress.Weights(Prepare.Unpack, false, 0)[JobSteps.Prepare];
                return await UnpackAsync(context, source, playlist);
            }

            case Prepare.Unpack:
                return await UnpackAsync(context, source, playlist);

            default:
                return await BackupAsync(context, source, decrypt: true);
        }
    }

    /// <summary>
    /// Asks the mount service to mount the ISO. Null when it could not and the settings say to
    /// unpack instead; when they say to ask (the default), the job stops with the question.
    /// </summary>
    private static async Task<string?> MountAsync(JobContext context, DiscSource source, PlaylistInfo playlist)
    {
        var settings = context.Options.IsoMount;
        context.Log($"Asking the mount service ({settings.Socket}) to mount {source.Path}.");
        var reply = await IsoMount.MountAsync(settings.Socket, settings.ToHost(source.Path), context.CancellationToken);
        string reason;
        if (reply is { Ok: true, Path: { } hostPath })
        {
            // MountedAt keeps the host path: that is what the service unmounts.
            context.Job.MountedAt = hostPath;
            var path = settings.ToContainer(hostPath);
            context.Save();
            var folder = SourceFinder.BdmvParent(path);
            if (folder != null && DiscAnalyzer.PlaylistPath(folder, playlist.Name) != null)
            {
                context.Log($"Mounted read-only at {path}: reading the disc there, nothing is copied.");
                return folder;
            }

            // Mounted on the host but not visible here: the container's /mnt/pc volume needs rslave.
            reason = $"The ISO was mounted at {path}, but this container does not see the playlist there " +
                     "(is the volume mounted with rslave?)";
            await UnmountAsync(context);
        }
        else
        {
            reason = reply.Error ?? "The mount service did not say why.";
        }

        if (settings.WhenItFails == MountFallback.Unpack)
        {
            context.Warn($"Could not mount the ISO ({reason.TrimEnd('.')}); unpacking it with 7-Zip instead.");
            return null;
        }

        throw new JobQuestionException(
            $"The ISO could not be mounted: {reason.TrimEnd('.')}. Unpack the playlist's clips with 7-Zip instead? " +
            $"That copies about {Format.Size(playlist.Size)} to {context.Options.WorkFolder} first.",
            $"Could not mount the ISO: {reason}");
    }

    /// <summary>Unmounts the job's ISO (when the job ends, or a mount that is no use).</summary>
    public static async Task UnmountAsync(JobContext context)
    {
        if (context.Job.MountedAt is not { } path)
        {
            return;
        }

        var reply = await IsoMount.UnmountAsync(context.Options.IsoMount.Socket, path, CancellationToken.None);
        if (reply.Ok)
        {
            context.Log($"Unmounted {path}.");
        }
        else
        {
            context.Warn($"Could not unmount {path}: {reply.Error} (unmount it on the host: sudo ripparr_mount.py unmount {path})");
        }

        context.Job.MountedAt = null;
        context.Save();
    }

    private static async Task<string> UnpackAsync(JobContext context, DiscSource source, PlaylistInfo playlist)
    {
        var tools = context.Options.Tools;
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
            // Unpacked anyway when the mount fails and the settings say not to ask.
            Prepare.Mount when context.Options.IsoMount.WhenItFails == MountFallback.Unpack => playlist.Size,
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

    private static async Task DryRunAsync(JobContext context, DiscSource source, PlaylistInfo playlist, Prepare prepare)
    {
        var tools = context.Options.Tools;
        context.Step("Dry run");
        var disc = prepare == Prepare.InPlace ? source.Path : context.DiscFolder;
        var unpack = prepare == Prepare.Unpack;
        if (prepare == Prepare.Mount)
        {
            var ping = await IsoMount.PingAsync(context.Options.IsoMount.Socket, context.CancellationToken);
            if (ping.Ok)
            {
                context.Log($"Mount: the mount service (version {ping.Version}) mounts the ISO read-only, DGDemux reads it there.");
                disc = Path.Combine(context.Options.IsoMount.Folder, "<the mounted ISO>");
            }
            else
            {
                unpack = true;
                context.Log($"Mount: {ping.Error} " + (context.Options.IsoMount.WhenItFails == MountFallback.Ask
                    ? "The job would stop and ask before unpacking with 7-Zip:"
                    : "The job would unpack with 7-Zip instead:"));
            }
        }

        if (prepare == Prepare.Decrypt)
        {
            context.Log("$ " + new ProcessSpec(tools.MakeMkvCon, ["-r", "--progress=-same", "--noscan", "backup", "--decrypt",
                MakeMkv.SourceArgument(source), context.DiscFolder]).CommandLine);
        }
        else if (unpack)
        {
            context.Log("$ " + new ProcessSpec(tools.SevenZip, ["x", source.Path, $"-o{context.DiscFolder}", "-y", "BDMV/PLAYLIST/*",
                "BDMV/CLIPINF/*", ..playlist.Mpls.Clips.Distinct().Select(c => $"BDMV/STREAM/{c}.m2ts")]).CommandLine);
        }

        var mpls = Path.Combine(disc, "BDMV", "PLAYLIST", playlist.Name + ".mpls");
        context.Log("$ " + new ProcessSpec(tools.DgDemux,
            DgDemuxCommand.Arguments(context.Job.Profile, context.Options.DgDemuxArguments, mpls, Path.Combine(context.DemuxFolder, playlist.Name),
                DgDemuxCommand.DemuxedPids(context.Job.Profile, context.Job.Plan))).CommandLine);
        var srt = context.Job.Plan.Kept.Where(t => SubtitleStep.WantsSrt(context.Job.Profile, t, context.Job.OriginalLanguage)).ToList();
        if (srt.Count > 0)
        {
            context.Log($"Then: Tesseract OCR of {srt.Count} subtitle track(s) into SRT{(context.Job.Profile.SrtRemoveSdh ? ", SDH text removed" : "")}: " +
                        string.Join(", ", srt.Select(t => $"\"{SubtitleStep.SrtName(context.Job.Profile, t)}\"")));
        }

        context.Log("Then: mediainfo on each stream, dovi_tool mux for the Dolby Vision layer, mkvmerge with these tracks:");
        foreach (var track in context.Job.Plan.Kept)
        {
            context.Log($"  {track.Kind} {track.Language} {track.Codec}{(track.Default ? " default" : "")}{(track.Forced ? " forced" : "")}" +
                        $"{(track.Commentary ? " commentary" : "")}{(track.HearingImpaired ? " SDH" : "")}{(track.VisualImpaired ? " AD" : "")}{(string.IsNullOrWhiteSpace(track.Name) ? "" : $" \"{track.Name}\"")}");
        }

        if (context.Job.Plan.MergeDolbyVisionLayer)
        {
            context.Log("  + the Dolby Vision enhancement layer, merged into the video");
        }

        if (context.Job.Profile.WriteTags && context.Job.Tags.Any(t => t.Value.Length > 0))
        {
            context.Log("  + your global tags: " + string.Join(", ", context.Job.Tags.Where(t => t.Value.Length > 0).Select(t => $"{t.Name}={t.Value}")));
        }

        if (context.Job.IsExtra)
        {
            context.Log($"Then: the extra goes to {Path.Combine(DoneFolder(context.Options.DoneFolder, context.Job,
                MuxBuilder.NameValues(context.Job, [])), Naming.SafeFileName(context.Job.ExtraName!) + ".mkv")}");
        }

        if (context.Job.IsEpisode)
        {
            var values = MuxBuilder.NameValues(context.Job, []);
            context.Log($"Then: the episode goes to {DoneFolder(context.Options.DoneFolder, context.Job, values)}/ as " +
                        $"{FileName(context.Job, values)} (the specs in brackets come from the demuxed tracks), " +
                        $"MKV title \"{MkvTitle(context.Job, values)}\"");
        }

        context.Log("Dry run: nothing was run.");
    }

    /// <summary>Splits the DGDemux argument setting on spaces and fills {input} and {output}.</summary>
    /// <summary>The stream's size on disk; mediainfo's figure when the file is gone.</summary>
    private static long FileSize(string path, StreamFacts facts)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return facts.StreamSize;
        }
    }

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
