using Ripparr.Subtitles;

namespace Ripparr.Tests;

[TestClass]
public class SubtitleClassifierTests
{
    private static List<OcrCaption> Read(params string[] texts) =>
        texts.Select((t, i) => new OcrCaption(i + 1, TimeSpan.FromSeconds(i * 5), TimeSpan.FromSeconds(i * 5 + 3), false, t, SdhCleaner.Clean(t))).ToList();

    private static PgsCaption Caption(int index, double start, double end, bool forced = false) => new()
    {
        Index = index, Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Forced = forced,
        Objects = [], Palette = new uint[256]
    };

    private static readonly string[] Dialogue =
    [
        "Where are you going?", "Home, to my family.", "I told you already.", "Give me the keys.", "Not tonight.",
        "We should leave now.", "Did you hear that?", "It's nothing.", "Come on, hurry up.", "I can't find it."
    ];

    [TestMethod]
    public void FindsSdh()
    {
        var texts = Dialogue.Concat(Dialogue).ToList();
        texts.AddRange(["[DOOR SLAMS]", "(LAUGHS) Fine.", "JOHN: Over here!", "♪ La la la ♪", "[PHONE RINGING]"]);
        var guess = SubtitleClassifier.Guess(Read(texts.ToArray()), false);
        Assert.IsTrue(guess.Sdh);
        Assert.IsFalse(guess.Commentary);
        Assert.AreEqual(5, guess.SdhCaptions);
    }

    [TestMethod]
    public void PlainDialogueIsNeither()
    {
        var guess = SubtitleClassifier.Guess(Read(Dialogue.Concat(Dialogue).Concat(Dialogue).ToArray()), false);
        Assert.IsFalse(guess.Sdh);
        Assert.IsFalse(guess.Commentary);
    }

    [TestMethod]
    public void FindsACommentary()
    {
        var texts = Dialogue.Concat(Dialogue).ToList();
        texts.AddRange([
            "We shot this scene in two days.", "The director wanted it darker.", "This was our first day on set.",
            "The actors improvised most of it.", "Originally the script had him die here.", "That shot took forty takes.",
            "On location in Prague it was freezing.", "The crew built the whole set."
        ]);
        var guess = SubtitleClassifier.Guess(Read(texts.ToArray()), false);
        Assert.IsTrue(guess.Commentary, guess.Reason);
        Assert.IsFalse(guess.Sdh);
    }

    [TestMethod]
    public void FindsAFrenchCommentary()
    {
        var texts = new List<string> { "Où vas-tu ?", "Chez moi.", "Je te l'ai dit.", "Donne-moi les clés.", "Pas ce soir." };
        texts.AddRange(texts);
        texts.AddRange(["Cette scène a été tournée à Lyon.", "Le réalisateur voulait plus de lumière.", "Le tournage a duré trois mois.",
            "Les acteurs étaient épuisés.", "La caméra était sur des rails.", "Le scénario a beaucoup changé."]);
        Assert.IsTrue(SubtitleClassifier.Guess(Read(texts.ToArray()), false).Commentary);
    }

    [TestMethod]
    public void AFewFilmWordsInDialogueNeedTheDiscClue()
    {
        // 6 of 50: 12%, under the 15% on its own, enough with a second, longer track of the same language.
        var texts = Enumerable.Repeat(Dialogue, 5).SelectMany(d => d).Take(44).ToList();
        texts.AddRange(["Get the camera.", "The director is here.", "Back to the set!", "Where are the actors?", "The script, please.", "The crew left."]);
        Assert.IsFalse(SubtitleClassifier.Guess(Read(texts.ToArray()), false).Commentary);
        Assert.IsTrue(SubtitleClassifier.Guess(Read(texts.ToArray()), true).Commentary);
    }

    [TestMethod]
    public void TooLittleTextSaysSo()
    {
        var guess = SubtitleClassifier.Guess(Read("Paris, 1944", "", ""), false);
        Assert.IsFalse(guess.Sdh);
        Assert.AreEqual("Only 1 captions read", guess.Reason);
    }

    [TestMethod]
    public void SamplesEvenly()
    {
        var captions = Enumerable.Range(1, 1000).Select(i => Caption(i, i, i + 1)).ToList();
        var sample = SubtitleClassifier.Sample(captions, 10);
        CollectionAssert.AreEqual(new[] { 51, 151, 251, 351, 451, 551, 651, 751, 851, 951 }, sample.Select(c => c.Index).ToArray());
        Assert.HasCount(5, SubtitleClassifier.Sample(captions.Take(5).ToList(), 10));
    }

    [TestMethod]
    public void LinesUpTracksByTime()
    {
        // Full track: three lines. Forced track: only the sign in the middle, a little later.
        var full = new List<PgsCaption> { Caption(1, 1, 3), Caption(2, 4, 6), Caption(3, 7, 9) };
        var forced = new List<PgsCaption> { Caption(1, 4.2, 6, forced: true) };
        var rows = CaptionAlignment.Align([full, forced]);

        Assert.HasCount(3, rows);
        Assert.IsNull(rows[0].Cells[1]);
        Assert.AreEqual(2, rows[1].Cells[0]!.Index);
        Assert.AreEqual(1, rows[1].Cells[1]!.Index);
        Assert.IsNull(rows[2].Cells[1]);
    }

    [TestMethod]
    public void TwoCaptionsOfOneTrackNeverShareARow()
    {
        var a = new List<PgsCaption> { Caption(1, 1, 5), Caption(2, 2, 6) };
        var rows = CaptionAlignment.Align([a]);
        Assert.HasCount(2, rows);
    }

    [TestMethod]
    public void ASecondLongerTrackOfALanguageIsAClue()
    {
        var tracks = new List<(int, string?, int)> { (4608, "eng", 1200), (4609, "fre", 1100), (4610, "eng", 1700), (4611, "eng", 1250) };
        Assert.IsFalse(Ripparr.Disc.SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4608));
        Assert.IsFalse(Ripparr.Disc.SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4609));
        Assert.IsTrue(Ripparr.Disc.SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4610));
        Assert.IsFalse(Ripparr.Disc.SubtitleScanner.MoreThanFirstOfLanguage(tracks, 4611));
    }
}
