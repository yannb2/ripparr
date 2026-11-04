using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Media;

namespace Ripparr.Profiles;

/// <summary>
/// The subtitle tracks' order: by the profile's languages first (as planned), then within each
/// language by kind (forced, full, SDH, commentary, in the profile's order), then text or
/// pictures first, then as on the disc.
/// </summary>
public static class SubtitleOrder
{
    public static readonly string[] DefaultKinds = ["forced", "full", "sdh", "commentary"];

    /// <summary>The words the profile page shows for each kind.</summary>
    public static string Label(string kind) => kind switch
    {
        "forced" => "Forced",
        "full" => "Full",
        "sdh" => "SDH",
        "commentary" => "Commentary",
        _ => kind
    };

    public static string Kind(PlannedTrack track) =>
        track.Commentary ? "commentary" : track.Forced ? "forced" : track.HearingImpaired ? "sdh" : "full";

    /// <summary>The profile's kinds, with any missing one added at its default place at the end.</summary>
    public static List<string> Kinds(RipProfile profile)
    {
        var kinds = profile.SubtitleKindOrder.Where(DefaultKinds.Contains).Distinct().ToList();
        kinds.AddRange(DefaultKinds.Where(k => !kinds.Contains(k)));
        return kinds;
    }

    public static int Rank(RipProfile profile, PlannedTrack track) => Kinds(profile).IndexOf(Kind(track));

    /// <summary>The outputs with the subtitle tracks sorted, in the places the subtitles had.</summary>
    public static List<OutputTrack> Sort(IReadOnlyList<OutputTrack> outputs, RipProfile profile)
    {
        var places = outputs.Select((o, i) => (o, i)).Where(x => x.o.Planned.Kind == StreamKind.Subtitle).Select(x => x.i).ToList();
        var subtitles = places.Select(i => outputs[i]).ToList();
        var languages = new List<string>();
        foreach (var track in subtitles)
        {
            var language = track.Planned.Language ?? "und";
            if (!languages.Any(l => Languages.Same(l, language)))
            {
                languages.Add(language);
            }
        }

        var kinds = Kinds(profile);
        var sorted = subtitles
            .Select((o, i) => (o, i))
            .OrderBy(x => languages.FindIndex(l => Languages.Same(l, x.o.Planned.Language ?? "und")))
            .ThenBy(x => kinds.IndexOf(Kind(x.o.Planned)))
            .ThenBy(x => IsText(x.o) == profile.SrtBeforePictures ? 0 : 1)
            .ThenBy(x => x.i)
            .Select(x => x.o)
            .ToList();

        var result = outputs.ToList();
        for (var i = 0; i < places.Count; i++)
        {
            result[places[i]] = sorted[i];
        }

        return result;
    }

    private static bool IsText(OutputTrack track) => string.Equals(track.Planned.Codec, "SRT", StringComparison.OrdinalIgnoreCase);
}
