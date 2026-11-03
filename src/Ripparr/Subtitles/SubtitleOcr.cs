using System.Globalization;
using Ripparr.Tools;

namespace Ripparr.Subtitles;

/// <summary>A caption's OCR result: what Tesseract read and what is left without the SDH text.</summary>
public sealed record OcrCaption(int Index, TimeSpan Start, TimeSpan End, bool Forced, string Text, string Clean);

/// <summary>
/// Turns PGS captions into text with Tesseract. Each caption becomes a grey picture with dark
/// text on white (what Tesseract reads best); the pictures go to Tesseract in batches through a
/// list file, so the language model loads once per batch, and batches run side by side.
/// </summary>
public static class SubtitleOcr
{
    private const int BatchSize = 150;

    /// <summary>ISO 639-2 (as in the MKV) to Tesseract's language names.</summary>
    public static string TesseractLanguage(string? code) => (code ?? "").ToLowerInvariant() switch
    {
        "fre" or "fra" or "fr" => "fra",
        "ger" or "deu" or "de" => "deu",
        "spa" or "es" => "spa",
        "ita" or "it" => "ita",
        "por" or "pt" => "por",
        "dut" or "nld" or "nl" => "nld",
        "cze" or "ces" => "ces",
        "chi" or "zho" => "chi_sim",
        "jpn" or "ja" => "jpn",
        "kor" or "ko" => "kor",
        "eng" or "en" or "" or "und" => "eng",
        var other => other
    };

    /// <summary>
    /// The caption as Tesseract likes it: light, opaque pixels (the letters) become dark, the
    /// outline and the transparent background white, with a margin; small text is doubled.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) ForOcr(RgbaImage image)
    {
        const int margin = 12;
        var scale = image.Height < 60 ? 2 : 1;
        var width = image.Width * scale + margin * 2;
        var height = image.Height * scale + margin * 2;
        var grey = new byte[width * height];
        Array.Fill(grey, (byte)255);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var c = image.Pixels[y * image.Width + x];
                var r = (c >> 24) & 0xFF;
                var g = (c >> 16) & 0xFF;
                var b = (c >> 8) & 0xFF;
                var a = c & 0xFF;
                var ink = (0.299 * r + 0.587 * g + 0.114 * b) * a / 255.0;
                var value = (byte)(255 - Math.Clamp(ink, 0, 255));
                for (var sy = 0; sy < scale; sy++)
                {
                    var row = (margin + y * scale + sy) * width + margin + x * scale;
                    for (var sx = 0; sx < scale; sx++)
                    {
                        grey[row + sx] = value;
                    }
                }
            }
        }

        return (grey, width, height);
    }

    /// <summary>
    /// Splits Tesseract's text output for a list of pictures: one page per picture, pages
    /// separated by form feeds.
    /// </summary>
    public static List<string> SplitPages(string output, int count)
    {
        var pages = output.Split('\f').Select(Tidy).ToList();
        while (pages.Count < count)
        {
            pages.Add("");
        }

        return pages.Take(count).ToList();
    }

    /// <summary>Lines trimmed, blank lines dropped, and the usual OCR slips of subtitle fonts fixed.</summary>
    public static string Tidy(string page)
    {
        var lines = page.Replace("\r", "").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s{2,}", " ")
                .Replace('|', 'I')
                .Replace("’", "'")
                .Replace("‘", "'")
                .Replace("“", "\"")
                .Replace("”", "\""))
            .Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    public static async Task<List<OcrCaption>> RecogniseAsync(IReadOnlyList<PgsCaption> captions, string tesseract, string language,
        string workFolder, Action<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workFolder);
        var texts = new string[captions.Count];
        var batches = captions.Select((c, i) => (Caption: c, Position: i)).Chunk(BatchSize).ToList();
        var done = 0;
        var parallel = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

        await Parallel.ForEachAsync(batches.Select((b, n) => (Batch: b, Number: n)),
            new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var folder = Path.Combine(workFolder, $"batch-{item.Number:D3}");
                Directory.CreateDirectory(folder);
                var files = new List<string>();
                foreach (var (caption, position) in item.Batch)
                {
                    var (pixels, width, height) = ForOcr(caption.Render());
                    var file = Path.Combine(folder, $"{position:D5}.png");
                    await File.WriteAllBytesAsync(file, Png.Grey(pixels, width, height), token);
                    files.Add(file);
                }

                var list = Path.Combine(folder, "list.txt");
                await File.WriteAllLinesAsync(list, files, token);
                var result = await ProcessRunner.RunAsync(new ProcessSpec(tesseract, [list, "stdout", "-l", language, "--psm", "6"])
                {
                    // One thread per process: the batches already run side by side.
                    Environment = new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" }
                }, TimeSpan.FromHours(1), null, token);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.NotStarted
                        ? "Tesseract is not installed."
                        : $"Tesseract failed (exit code {result.ExitCode}): {result.LastLine}");
                }

                var pages = SplitPages(result.Output, files.Count);
                for (var i = 0; i < item.Batch.Length; i++)
                {
                    texts[item.Batch[i].Position] = pages[i];
                }

                Directory.Delete(folder, recursive: true);
                progress?.Invoke(Interlocked.Add(ref done, item.Batch.Length) / (double)captions.Count);
            });

        return captions.Select((c, i) => new OcrCaption(c.Index, c.Start, c.End, c.Forced, texts[i] ?? "", SdhCleaner.Clean(texts[i] ?? "")))
            .ToList();
    }

    public static string Describe(IReadOnlyList<OcrCaption> captions)
    {
        var changed = captions.Count(c => SdhCleaner.HasSdh(c.Text));
        var dropped = captions.Count(c => c.Clean.Length == 0 && c.Text.Length > 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{captions.Count} captions, SDH removed from {changed} ({dropped} left empty and dropped)");
    }
}
