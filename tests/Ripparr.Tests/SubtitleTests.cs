using Ripparr.Jobs;
using Ripparr.Disc;
using Arrkit.Subtitles;

namespace Ripparr.Tests;

/// <summary>
/// Ripparr's own use of the subtitle code. The PGS reader, OCR, SDH cleaner and classifier are
/// tested in arrkit.
/// </summary>
[TestClass]
public class SubtitleTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "Samples", "captions.sup");

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

    [TestMethod]
    public void ASecondLongerTrackOfALanguageIsAClue()
    {
        var tracks = new List<(int, string?, int)> { (4608, "eng", 1200), (4609, "fre", 1100), (4610, "eng", 1700), (4611, "eng", 1250) };
        Assert.IsFalse(SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4608));
        Assert.IsFalse(SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4609));
        Assert.IsTrue(SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4610));
        Assert.IsFalse(SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4611));
    }
}
