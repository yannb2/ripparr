using Ripparr.Jobs;
using Ripparr.Disc;
using Ripparr.Subtitles;

namespace Ripparr.Tests;

[TestClass]
public class SubtitleTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "Samples", "captions.sup");

    [TestMethod]
    public void ReadsCaptionsFromASupFile()
    {
        var captions = PgsReader.Read(Sample);
        Assert.HasCount(4, captions);
        Assert.AreEqual(TimeSpan.FromSeconds(1), captions[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3), captions[0].End);
        Assert.AreEqual(TimeSpan.FromSeconds(6.5), captions[1].End);
        Assert.IsFalse(captions[0].Forced);
        Assert.IsTrue(captions[3].Forced);
        Assert.AreEqual(1920, captions[0].ScreenWidth);

        var image = captions[0].Render();
        Assert.AreEqual(captions[0].Bounds.Width, image.Width);
        Assert.IsTrue(image.Pixels.Any(p => p == PgsReader.ToRgba(235, 128, 128, 255)), "white letters");
        Assert.IsTrue(image.Pixels.Any(p => (p & 0xFF) == 0), "transparent background");
    }

    [TestMethod]
    public void DecodesEveryRunKind()
    {
        // 2 pixels colour 5, run of 3 x colour 0, run of 2 x colour 7, end of line; a long run of 70 x colour 9.
        byte[] rle = [5, 5, 0, 3, 0, 0x82, 7, 0, 0, 0, 0xC0, 70, 9, 0, 0];
        var pixels = PgsReader.DecodeRle(rle, 70, 2);
        CollectionAssert.AreEqual(new byte[] { 5, 5, 0, 0, 0, 7, 7, 0 }, pixels[..8]);
        Assert.IsTrue(pixels[70..].All(p => p == 9));
    }

    [TestMethod]
    public void WritesPngFiles()
    {
        var png = Png.Rgba(PgsReader.Read(Sample)[1].Render());
        CollectionAssert.AreEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        var grey = SubtitleOcr.ForOcr(PgsReader.Read(Sample)[1].Render());
        Assert.AreEqual(255, grey.Pixels[0], "white margin");
        Assert.IsTrue(grey.Pixels.Any(p => p < 64), "dark letters");
    }

    [TestMethod]
    public void RemovesHearingImpairedText()
    {
        Assert.AreEqual("Hello there, Sarah.", SdhCleaner.Clean("[DOOR SLAMS]\nJOHN: Hello there, Sarah."));
        Assert.AreEqual("- Where are you going?\n- Home.", SdhCleaner.Clean("- Where are you going?\n- (SIGHS) Home."));
        Assert.AreEqual("Home.", SdhCleaner.Clean("- [laughs]\n- Home."));
        Assert.AreEqual("", SdhCleaner.Clean("♪ La la la ♪"));
        Assert.AreEqual("", SdhCleaner.Clean("(DISTANT SIREN\nWAILING)"));
        Assert.AreEqual("I'll be there at 10:30.", SdhCleaner.Clean("I'll be there at 10:30."));
        Assert.AreEqual("Okay.", SdhCleaner.Clean("MAN #2: Okay."));
        Assert.IsTrue(SdhCleaner.HasSdh("[DOOR SLAMS]\nHi."));
        Assert.IsFalse(SdhCleaner.HasSdh("Just a line."));
    }

    [TestMethod]
    public void WritesSrt()
    {
        var srt = Srt.Write([
            new SrtCue(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), "Hello."),
            new SrtCue(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), ""),
            new SrtCue(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(7), TimeSpan.FromSeconds(3603.6), "- Yes?\n- No.")
        ]);
        Assert.AreEqual("1\n00:00:01,500 --> 00:00:03,000\nHello.\n\n2\n01:00:00,007 --> 01:00:03,600\n- Yes?\n- No.\n\n", srt);
    }

    [TestMethod]
    public void SplitsTesseractPages()
    {
        CollectionAssert.AreEqual(new[] { "Hello", "I said\nhi", "" }, SubtitleOcr.SplitPages("Hello\n\n\f|  said\nhi\n\f", 3));
        Assert.AreEqual("fra", SubtitleOcr.TesseractLanguage("fre"));
        Assert.AreEqual("eng", SubtitleOcr.TesseractLanguage("und"));
    }

    [TestMethod]
    public async Task ReadsCaptionsWithTesseract()
    {
        var available = await Ripparr.Tools.ProcessRunner.RunAsync("tesseract", ["--version"], TimeSpan.FromSeconds(10));
        if (!available.Success)
        {
            Assert.Inconclusive("Tesseract is not installed here (it is in the image).");
        }

        var folder = Path.Combine(Path.GetTempPath(), "ripparr-ocr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ocr = await SubtitleOcr.RecogniseAsync(PgsReader.Read(Sample), "tesseract", "eng", folder, null, CancellationToken.None);
            Assert.HasCount(4, ocr);
            StringAssert.Contains(ocr[0].Text, "Hello there");
            Assert.AreEqual("Hello there, Sarah.", ocr[0].Clean);
            StringAssert.Contains(ocr[1].Clean, "Where are you going?");
            Assert.AreEqual("Paris, 1944", ocr[3].Clean);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [TestMethod]
    public void ReadsABareSupThatMediaInfoCannot()
    {
        var facts = MuxBuilder.SupFacts(Sample);
        Assert.IsNotNull(facts);
        Assert.AreEqual(StreamKind.Subtitle, facts.Kind);
        Assert.AreEqual("PGS", facts.Format);
        Assert.AreEqual(PgsReader.Read(Sample).Count, facts.ElementCount);
        Assert.IsTrue(MuxBuilder.IsSubtitleFile("/x/00800.4608.SUP"));

        var notPgs = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}.sup");
        File.WriteAllText(notPgs, "hello, not a subtitle");
        try
        {
            Assert.IsNull(MuxBuilder.SupFacts(notPgs));
        }
        finally
        {
            File.Delete(notPgs);
        }
    }
}
