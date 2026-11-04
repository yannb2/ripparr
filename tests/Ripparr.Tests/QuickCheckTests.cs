using Ripparr.Disc;
using Ripparr.Media;
using Arrkit.Subtitles;

namespace Ripparr.Tests;

[TestClass]
public class QuickCheckTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const int Slice = 32 * 1024 * 1024;

    [TestMethod]
    public void SlicesAreSpreadOverEveryClipAndAligned()
    {
        var plan = SubtitleScanner.SlicePlan([30 * Gb, 10 * Gb], 32, Slice);

        Assert.AreEqual(32, plan.Count);
        Assert.AreEqual(24, plan.Count(p => p.Item == 0), "three quarters of the bytes, three quarters of the slices");
        Assert.AreEqual(8, plan.Count(p => p.Item == 1));
        Assert.IsTrue(plan.All(p => p.Offset % EncryptionCheck.AlignedUnit == 0));
        Assert.IsTrue(plan.Where(p => p.Item == 0).All(p => p.Offset + Slice <= 30 * Gb));
        Assert.IsTrue(plan.Where(p => p.Item == 0).Max(p => p.Offset) > 25 * Gb, "reaches the end of the film");
    }

    [TestMethod]
    public void ASmallClipIsReadOnce()
    {
        var plan = SubtitleScanner.SlicePlan([Slice / 2], 32, Slice);

        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual((0, 0L), plan[0]);
        Assert.AreEqual(0, SubtitleScanner.SlicePlan([0], 32, Slice).Count);
    }

    [TestMethod]
    public void TheQuickCheckIsKeptApartFromTheWholeFilmPreview()
    {
        var source = new DiscSource("/downloads/Film", SourceKind.Folder, "/downloads", 1, DateTime.UtcNow);
        var playlist = new PlaylistInfo { Name = "00800", Mpls = Mpls.Parse(MplsBuilder.UhdFilm(100).Build()) };

        Assert.AreNotEqual(SubtitleScanner.KeyFor(source, playlist), SubtitleScanner.KeyFor(source, playlist, quick: true));
        Assert.AreEqual(SubtitleScanner.KeyFor(source, playlist, quick: true), SubtitleScanner.KeyFor(source, playlist, quick: true));
    }

    [TestMethod]
    public void QuebecFrenchFromItsWords()
    {
        var hint = FrenchRegion.Guess(["On se voit en fin de semaine ?", "Je t'envoie un courriel.", "Il est où, mon char ?"]);

        Assert.AreEqual("CA", hint.Region);
        CollectionAssert.IsSubsetOf(new[] { "fin de semaine", "courriel", "char" }, hint.Quebec.ToList());
    }

    [TestMethod]
    public void FranceFrenchFromItsWords()
    {
        var hint = FrenchRegion.Guess(["Ce week-end, je bosse.", "Laisse ta bagnole au parking.", "Appelle-moi sur mon portable."]);

        Assert.AreEqual("FR", hint.Region);
        Assert.AreEqual(0, hint.Quebec.Count);
    }

    [TestMethod]
    public void NoHintWithoutEnoughWords()
    {
        Assert.IsNull(FrenchRegion.Guess(["Bonjour, comment allez-vous ?", "Très bien, merci."]).Region);
        Assert.IsNull(FrenchRegion.Guess(["Un courriel."]).Region, "one word is not enough");
        Assert.IsNull(FrenchRegion.Guess(["Un bec dans le char."]).Region, "bec and char exist in France too");
        Assert.IsNull(FrenchRegion.Guess(["Un courriel au parking.", "Le stationnement est loin, viens ce week-end."]).Region, "a tie");
    }

    [TestMethod]
    public void AccentsAndCaseDoNotMatter()
    {
        Assert.AreEqual("presentement au depanneur", FrenchRegion.Plain("PRÉSENTEMENT, au dépanneur !"));
        Assert.AreEqual("CA", FrenchRegion.Guess(["Présentement, je suis au DÉPANNEUR."]).Region);
    }

    [TestMethod]
    public void FrenchOfCanadaIsStillFrench()
    {
        Assert.IsTrue(Languages.Same("fr-CA", "fre"));
        Assert.IsTrue(Languages.Same("fr-CA", "fra"));
        Assert.IsFalse(Languages.Same("fr-CA", "eng"));
        Assert.AreEqual("fra", Languages.Base("fr-CA"));
        Assert.AreEqual("fra", Languages.Base("fre"));
        Assert.AreEqual("CA", Languages.Region("fr-CA"));
        Assert.IsNull(Languages.Region("fre"));
        Assert.IsTrue(Languages.IsFrench("fr-CA"));
        Assert.IsTrue(Languages.IsFrench("fre"));
        Assert.IsFalse(Languages.IsFrench("eng"));
        Assert.IsFalse(Languages.IsFrench(""));
        Assert.AreEqual("French (Canada)", Languages.Name("fr-CA"));
        Assert.AreEqual("fra", SubtitleOcr.TesseractLanguage("fr-CA"));
    }
}
