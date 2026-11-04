using System.Text.Json;
using Ripparr.Disc;
using Ripparr.Media;
using Ripparr.Profiles;
using Arrkit.Subtitles;

namespace Ripparr.Jobs;

/// <summary>
/// Keeps every subtitle stream of the job for the preview and, for the profile's SRT languages,
/// reads the pictures with Tesseract and adds a text track right after the picture track.
/// An OCR problem is a warning: the picture subtitles still go into the MKV.
/// </summary>
public static class SubtitleStep
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Folder(Options.RipparrOptions options, long jobId) =>
        Path.Combine(options.SubtitleFolder, $"job-{jobId}");

    /// <summary>Does the profile want an SRT of this track?</summary>
    public static bool WantsSrt(RipProfile profile, PlannedTrack track, string? originalLanguage)
    {
        if (track.Kind != StreamKind.Subtitle || (track.Forced && !profile.SrtForForced))
        {
            return false;
        }

        return profile.SrtLanguages.Any(l => l == "original"
            ? originalLanguage != null && Languages.Same(originalLanguage, track.Language)
            : Languages.Same(l, track.Language));
    }

    /// <summary>The SRT's name: the subtitle templates with {codec} = SRT. With the SDH text removed it is no longer SDH.</summary>
    public static string SrtName(RipProfile profile, PlannedTrack track, bool? sdhRemoved = null) =>
        TrackNaming.Subtitle(profile, Languages.Name(track.Language), "SRT",
            new TrackFlags(HearingImpaired: track.HearingImpaired && !(sdhRemoved ?? profile.SrtRemoveSdh), Forced: track.Forced, Commentary: track.Commentary),
            string.IsNullOrWhiteSpace(track.Name) ? null : track.Name.Trim());

    /// <summary>What the first pass learned about one picture subtitle track.</summary>
    private sealed class Read(OutputTrack output, string key, List<PgsCaption> captions)
    {
        public OutputTrack Output { get; set; } = output;
        public string Key { get; } = key;
        public List<PgsCaption> Captions { get; } = captions;
        public List<OcrCaption>? Ocr { get; set; }

        /// <summary>An SRT of it without the SDH text is in the MKV already.</summary>
        public bool CleanSrt { get; set; }

        /// <summary>Its SDH text, when known: marked on the job, or found by the OCR.</summary>
        public bool Sdh { get; set; }

        public PlannedTrack Track => Output.Planned;
    }

    /// <summary>
    /// The languages whose full subtitle tracks (not forced, not commentary) are all SDH and have
    /// no SRT without the SDH text yet: the first track of each, which gets a clean SRT.
    /// </summary>
    internal static IEnumerable<T> OnlySdh<T>(IEnumerable<T> tracks, Func<T, PlannedTrack> planned, Func<T, bool> sdh, Func<T, bool> cleanSrt) =>
        tracks.Where(t => !planned(t).Forced && !planned(t).Commentary)
            .GroupBy(t => Languages.Base(planned(t).Language))
            .Where(g => g.Key != "und" && g.All(sdh) && !g.Any(cleanSrt))
            .Select(g => g.First());

    /// <summary>True when enough captions carry SDH text ([door slams], JOHN:, ♪) to call the track SDH: a tenth, and at least 5.</summary>
    public static bool LooksSdh(IReadOnlyList<OcrCaption> captions)
    {
        var withText = captions.Count(c => c.Text.Length > 0);
        var sdh = captions.Count(c => SdhCleaner.HasSdh(c.Text));
        return sdh >= 5 && sdh * 10 >= withText;
    }

    public static async Task<List<OutputTrack>> RunAsync(JobContext context, List<OutputTrack> outputs)
    {
        var job = context.Job;
        var profile = job.Profile;
        var folder = Folder(context.Options, job.Id);
        var subtitles = outputs.Where(o => o.Planned.Kind == StreamKind.Subtitle
                                           && Path.GetExtension(o.File.Path).Equals(".sup", StringComparison.OrdinalIgnoreCase)).ToList();
        job.Subtitles = [];
        if (subtitles.Count == 0)
        {
            context.Log("No picture subtitles to keep.");
            return outputs;
        }

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Warn($"No subtitle preview: {folder} cannot be written ({e.Message}).");
            return outputs;
        }

        var result = new List<OutputTrack>(outputs);
        var wanted = subtitles.Count(s => WantsSrt(profile, s.Planned, job.OriginalLanguage));
        var ocrDone = 0;
        var reads = new List<Read>();
        for (var n = 0; n < subtitles.Count; n++)
        {
            var output = subtitles[n];
            var track = output.Planned;
            var key = $"{n + 1:D2}-{track.Language}{(track.Forced ? "-forced" : "")}";
            List<PgsCaption> captions;
            try
            {
                File.Copy(output.File.Path, Path.Combine(folder, key + ".sup"), overwrite: true);
                captions = PgsReader.Read(output.File.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                context.Warn($"Subtitle {key}: {e.Message}");
                continue;
            }

            var read = new Read(output, key, captions) { Sdh = track.HearingImpaired };
            reads.Add(read);
            var entry = new JobSubtitle(key, track.Language, output.Name, track.Forced, captions.Count);
            context.Log($"Subtitle {key} \"{output.Name}\": {captions.Count} captions ({captions.Count(c => c.Forced)} forced).");

            if (WantsSrt(profile, track, job.OriginalLanguage) && captions.Count > 0)
            {
                var language = SubtitleOcr.TesseractLanguage(track.Language);
                context.Log($"  OCR with Tesseract ({language}){(profile.SrtRemoveSdh ? ", SDH text removed" : "")}…");
                var started = ocrDone;
                try
                {
                    var ocr = await SubtitleOcr.RecogniseAsync(captions, context.Options.Tools.Tesseract, language,
                        Path.Combine(context.WorkFolder, "ocr"), p => context.Progress((started + p) / Math.Max(wanted, 1)),
                        context.CancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(folder, key + ".ocr.json"), JsonSerializer.Serialize(ocr, Json),
                        context.CancellationToken);

                    read.Ocr = ocr;
                    read.Sdh = track.HearingImpaired || LooksSdh(ocr);
                    read.CleanSrt = profile.SrtRemoveSdh;
                    if (profile.DetectSdh && !track.HearingImpaired && LooksSdh(ocr))
                    {
                        track.HearingImpaired = true;
                        var renamed = output with { Name = MuxBuilder.TrackName(track, output.File.Facts, profile, job.OriginalLanguage) };
                        result[result.IndexOf(output)] = renamed;
                        output = renamed;
                        read.Output = renamed;
                        entry = entry with { Name = renamed.Name };
                        context.Log($"  SDH text in many captions: marked as SDH, now \"{renamed.Name}\".");
                    }

                    var cues = ocr.Select(c => new SrtCue(c.Start, c.End, profile.SrtRemoveSdh ? c.Clean : c.Text)).ToList();
                    var srt = Srt.Write(cues);
                    var srtPath = Path.Combine(context.DemuxFolder, key + ".srt");
                    await File.WriteAllTextAsync(srtPath, srt, context.CancellationToken);
                    File.Copy(srtPath, Path.Combine(folder, key + ".srt"), overwrite: true);

                    var written = cues.Count(c => !string.IsNullOrWhiteSpace(c.Text));
                    var srtTrack = new PlannedTrack
                    {
                        StreamIndex = track.StreamIndex, Kind = StreamKind.Subtitle, Pid = track.Pid, Codec = "SRT",
                        Language = track.Language, Keep = true, Forced = track.Forced, Commentary = track.Commentary,
                        HearingImpaired = track.HearingImpaired && !profile.SrtRemoveSdh, Default = false
                    };
                    var facts = new StreamFacts { Kind = StreamKind.Subtitle, Format = "SRT (OCR)", ElementCount = written };
                    var name = SrtName(profile, track);
                    result.Insert(result.IndexOf(output) + 1, new OutputTrack(srtTrack, new DemuxedFile(srtPath, facts), name));

                    var summary = SubtitleOcr.Describe(ocr);
                    entry = entry with { Ocr = true, SrtName = name, OcrSummary = summary };
                    context.Log($"  {summary}; {written} cues in the SRT \"{name}\".");
                }
                catch (Exception e) when (e is InvalidOperationException or IOException)
                {
                    context.Warn($"No SRT for subtitle {key}: {e.Message}");
                }

                ocrDone++;
            }

            job.Subtitles.Add(entry);
        }

        if (profile.SrtCleanWhenOnlySdh)
        {
            await AddCleanSrtsAsync(context, reads, result, folder);
        }

        Prune(context.Options, context);
        return result;
    }

    /// <summary>
    /// A language with nothing but SDH subtitles gets an SRT with the SDH text removed. A track the
    /// first pass did not read is checked on a sample of its captions first (a few seconds), and
    /// read in full only when it turns out to be SDH.
    /// </summary>
    private static async Task AddCleanSrtsAsync(JobContext context, List<Read> reads, List<OutputTrack> result, string folder)
    {
        var job = context.Job;
        var profile = job.Profile;
        var ocrFolder = Path.Combine(context.WorkFolder, "ocr");
        var full = reads.Where(r => !r.Track.Forced && !r.Track.Commentary && r.Captions.Count > 0).ToList();
        try
        {
            // Unread tracks of a language that has no plain read track yet: is it SDH?
            foreach (var read in full.Where(r => r is { Sdh: false, Ocr: null }))
            {
                if (full.Any(o => o != read && Languages.Same(o.Track.Language, read.Track.Language) && o is { Sdh: false, Ocr: not null }))
                {
                    continue;
                }

                var sample = await SubtitleOcr.RecogniseAsync(SubtitleClassifier.Sample(read.Captions), context.Options.Tools.Tesseract,
                    SubtitleOcr.TesseractLanguage(read.Track.Language), ocrFolder, null, context.CancellationToken);
                read.Sdh = LooksSdh(sample);
                context.Log($"Subtitle {read.Key}: {sample.Count} captions read to check for SDH text: {(read.Sdh ? "SDH" : "not SDH")}.");
                if (read.Sdh && profile.DetectSdh && !read.Track.HearingImpaired)
                {
                    read.Track.HearingImpaired = true;
                    var renamed = read.Output with { Name = MuxBuilder.TrackName(read.Track, read.Output.File.Facts, profile, job.OriginalLanguage) };
                    result[result.IndexOf(read.Output)] = renamed;
                    read.Output = renamed;
                    Rename(job, read.Key, renamed.Name);
                    context.Log($"Subtitle {read.Key}: SDH text in many sampled captions, marked as SDH, now \"{renamed.Name}\".");
                }
            }

            foreach (var read in OnlySdh(full, r => r.Track, r => r.Sdh, r => r.CleanSrt).ToList())
            {
                var language = Languages.Name(read.Track.Language);
                context.Log($"Only SDH subtitles in {language}: an SRT without the SDH text is added from {read.Key}.");
                var ocr = read.Ocr ?? await SubtitleOcr.RecogniseAsync(read.Captions, context.Options.Tools.Tesseract,
                    SubtitleOcr.TesseractLanguage(read.Track.Language), ocrFolder, null, context.CancellationToken);
                var cues = ocr.Select(c => new SrtCue(c.Start, c.End, c.Clean)).ToList();
                var written = cues.Count(c => !string.IsNullOrWhiteSpace(c.Text));
                var file = read.Ocr == null ? read.Key : read.Key + ".clean";
                var srtPath = Path.Combine(context.DemuxFolder, file + ".srt");
                await File.WriteAllTextAsync(srtPath, Srt.Write(cues), context.CancellationToken);
                File.Copy(srtPath, Path.Combine(folder, file + ".srt"), overwrite: true);

                var srtTrack = new PlannedTrack
                {
                    StreamIndex = read.Track.StreamIndex, Kind = StreamKind.Subtitle, Pid = read.Track.Pid, Codec = "SRT",
                    Language = read.Track.Language, Keep = true, Default = false
                };
                var name = SrtName(profile, read.Track, sdhRemoved: true);
                var facts = new StreamFacts { Kind = StreamKind.Subtitle, Format = "SRT (OCR)", ElementCount = written };

                // Right after the picture track and the SRT already made of it, if any.
                var at = result.IndexOf(read.Output) + 1;
                while (at < result.Count && result[at].Planned.Codec == "SRT" && result[at].Planned.Pid == read.Track.Pid)
                {
                    at++;
                }

                result.Insert(at, new OutputTrack(srtTrack, new DemuxedFile(srtPath, facts), name));
                if (read.Ocr == null)
                {
                    await File.WriteAllTextAsync(Path.Combine(folder, read.Key + ".ocr.json"), JsonSerializer.Serialize(ocr, Json),
                        context.CancellationToken);
                    var index = job.Subtitles.FindIndex(s => s.Key == read.Key);
                    if (index >= 0)
                    {
                        job.Subtitles[index] = job.Subtitles[index] with { Ocr = true, SrtName = name, OcrSummary = SubtitleOcr.Describe(ocr) };
                    }
                }

                read.CleanSrt = true;
                context.Log($"  {written} cues in the SRT \"{name}\".");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            context.Warn($"No SRT without SDH text: {e.Message}");
        }
    }

    private static void Rename(Job job, string key, string name)
    {
        var index = job.Subtitles.FindIndex(s => s.Key == key);
        if (index >= 0)
        {
            job.Subtitles[index] = job.Subtitles[index] with { Name = name };
        }
    }

    /// <summary>Deletes the subtitle previews of all but the most recent jobs.</summary>
    private static void Prune(Options.RipparrOptions options, JobContext context)
    {
        try
        {
            var old = new DirectoryInfo(options.SubtitleFolder).GetDirectories("job-*")
                .Select(d => (Dir: d, Id: long.TryParse(d.Name[4..], out var id) ? id : 0))
                .OrderByDescending(d => d.Id)
                .Skip(Math.Max(options.KeepSubtitlePreviews, 1));
            foreach (var (dir, _) in old)
            {
                dir.Delete(recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Warn($"Could not tidy {options.SubtitleFolder}: {e.Message}");
        }
    }

    /// <summary>A preview file of a job, when the key is one the job wrote (no paths from the address bar).</summary>
    public static string? PreviewFile(Options.RipparrOptions options, Job job, string key, string extension)
    {
        if (job.Subtitles.All(s => s.Key != key))
        {
            return null;
        }

        var path = Path.Combine(Folder(options, job.Id), key + extension);
        return File.Exists(path) ? path : null;
    }

    public static List<OcrCaption> ReadOcr(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<OcrCaption>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }
}
