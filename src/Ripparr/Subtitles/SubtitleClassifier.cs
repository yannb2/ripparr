using System.Globalization;
using System.Text.RegularExpressions;

namespace Ripparr.Subtitles;

/// <summary>What the OCR of a sample of a track's captions says about the track.</summary>
public sealed record TrackGuess(bool Sdh, bool Commentary, string Reason, int Sampled = 0, int SdhCaptions = 0, int CommentaryCaptions = 0);

/// <summary>
/// Tells SDH and commentary subtitle tracks from the plain ones by what their captions say.
/// SDH: sound and speaker labels ([door slams], (laughs), JOHN:, ♪) in a tenth of the captions,
/// the same rule as the job's OCR. Commentary: people talking about making the film ("this
/// scene", "we shot", "on set", "the director") in many captions, which film dialogue rarely does
/// so often; a second track in the same language with clearly more captions than the first
/// backs it up. Both are guesses: the job form shows why, and the flags can be unticked.
/// </summary>
public static partial class SubtitleClassifier
{
    /// <summary>How many captions, spread over the track, are read.</summary>
    public const int SampleSize = 120;

    /// <summary>Evenly spread captions, all of them when the track is short.</summary>
    public static List<PgsCaption> Sample(IReadOnlyList<PgsCaption> captions, int size = SampleSize)
    {
        if (captions.Count <= size)
        {
            return captions.ToList();
        }

        return Enumerable.Range(0, size).Select(i => captions[(int)((i + 0.5) * captions.Count / size)]).ToList();
    }

    /// <param name="sample">The OCR of the sampled captions.</param>
    /// <param name="moreThanFirstOfLanguage">
    /// This track has clearly more captions (a third more) than an earlier track in the same
    /// language: how a commentary transcript usually sits on a disc.
    /// </param>
    public static TrackGuess Guess(IReadOnlyList<OcrCaption> sample, bool moreThanFirstOfLanguage)
    {
        var read = sample.Where(c => c.Text.Length > 0).ToList();
        if (read.Count < 5)
        {
            return new TrackGuess(false, false, read.Count == 0 ? "No text read" : $"Only {read.Count} captions read", read.Count);
        }

        var sdh = read.Count(c => SdhCleaner.HasSdh(c.Text));
        var talk = read.Count(c => MakingOf().IsMatch(c.Text));
        var isSdh = sdh >= 5 && sdh * 10 >= read.Count;
        var share = talk / (double)read.Count;
        var isCommentary = talk >= 6 && (share >= 0.15 || (share >= 0.08 && moreThanFirstOfLanguage));

        var reasons = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{read.Count} captions read: {sdh} with SDH text"),
            string.Create(CultureInfo.InvariantCulture, $"{talk} talking about the making of the film")
        };
        if (moreThanFirstOfLanguage)
        {
            reasons.Add("more captions than the first track in its language");
        }

        return new TrackGuess(isSdh, isCommentary, string.Join(", ", reasons), read.Count, sdh, talk);
    }

    /// <summary>Words of a commentary: the shoot, the crew, the cast, the script; English, French, German, Spanish, Italian.</summary>
    [GeneratedRegex(
        @"\b(this|that|the|next|first|opening|whole) (scene|shot|sequence|take)s?\b" +
        @"|\b(we|they|i) (shot|filmed|cut|reshot|recorded|rehearsed|cast|wrote)\b" +
        @"|\b(shooting|filming|on set|the set|on location|the script|the screenplay|the director|the producer|the studio|the crew|the cast|the actors?|the actress|the editor|the camera|the lens|the storyboard|visual effects|cgi|green ?screen|post-?production|the final cut|deleted scene|the dailies|the audience|the first draft|the shoot)\b" +
        @"|\b(cette|la|une|la prochaine) (scène|séquence|prise)\b|\b(le tournage|on a tourné|nous avons tourné|le plateau|le réalisateur|la réalisatrice|le producteur|les acteurs|les comédiens|la caméra|le scénario|le montage|les effets spéciaux)\b" +
        @"|\b(diese|die|der) (szene|einstellung|aufnahme)\b|\b(der dreh|gedreht|am set|der regisseur|die schauspieler|die kamera|das drehbuch|der schnitt)\b" +
        @"|\b(esta|la) (escena|toma|secuencia)\b|\b(el rodaje|rodamos|el director|los actores|la cámara|el guion|el montaje)\b" +
        @"|\b(questa|la) (scena|inquadratura|ripresa)\b|\b(le riprese|abbiamo girato|il regista|gli attori|la macchina da presa|la sceneggiatura|il montaggio)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MakingOf();
}
