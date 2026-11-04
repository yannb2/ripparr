namespace Ripparr.Disc;

/// <summary>A playlist with what Ripparr makes of it.</summary>
public sealed class PlaylistInfo
{
    /// <summary>The file name without .mpls ("00800").</summary>
    public required string Name { get; init; }

    public required Mpls Mpls { get; init; }

    /// <summary>The size of its clips, counted once each.</summary>
    public long Size { get; set; }

    /// <summary>Ripparr's guess for the film.</summary>
    public bool MainFeature { get; set; }

    /// <summary>Another playlist with the same clips and length, listed instead of this one.</summary>
    public string? DuplicateOf { get; set; }

    /// <summary>Long, shares most clips with the main feature but runs longer or shorter: probably another cut.</summary>
    public bool PossibleCut { get; set; }

    /// <summary>Shorter than the minimum length (menus, logos, trailers).</summary>
    public bool Short { get; set; }

    /// <summary>"Play all": its clips are other playlists one after the other (all the extras in a row).</summary>
    public bool PlayAll { get; set; }

    public TimeSpan Duration => Mpls.Duration;

    /// <summary>A copy for one job, with its playlist corrected (the shared analysis stays as read).</summary>
    public PlaylistInfo WithMpls(Mpls mpls) => new()
    {
        Name = Name, Mpls = mpls, Size = Size, MainFeature = MainFeature, DuplicateOf = DuplicateOf,
        PossibleCut = PossibleCut, Short = Short, PlayAll = PlayAll
    };

    public string? Resolution => Mpls.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video && !s.DolbyVisionEnhancementLayer) is { } v
        ? StreamCoding.Resolution(v.Format)
        : null;
}

public sealed class DiscAnalysis
{
    public required DiscSource Source { get; init; }

    /// <summary>The title the disc gives itself (BDMV/META/DL/bdmt_eng.xml), if any.</summary>
    public string? DiscTitle { get; set; }

    public Encryption Encryption { get; set; }

    public bool HasAacsFolder { get; set; }

    public List<PlaylistInfo> Playlists { get; set; } = [];

    /// <summary>Clip name ("00055") to the size of its .m2ts file.</summary>
    public Dictionary<string, long> ClipSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Many long playlists of nearly the same length: a disc that hides its film among fakes.</summary>
    public bool ManyPlaylists { get; set; }

    public string? Error { get; set; }

    public List<string> Notes { get; set; } = [];

    public PlaylistInfo? MainFeature => Playlists.FirstOrDefault(p => p.MainFeature);

    public PlaylistInfo? Find(string name) => Playlists.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// Picks the main feature (the longest, then the one with most chapters), marks playlists
    /// that repeat another one, short ones, and long ones that look like another cut.
    /// </summary>
    public void Classify(TimeSpan minimum)
    {
        foreach (var playlist in Playlists)
        {
            playlist.Size = playlist.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase)
                .Sum(c => ClipSizes.GetValueOrDefault(c));
            playlist.Short = playlist.Duration < minimum;
            playlist.MainFeature = false;
            playlist.PossibleCut = false;
            playlist.DuplicateOf = null;
            playlist.PlayAll = false;
        }

        var ordered = Playlists.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (ordered[j].DuplicateOf == null && SameContent(ordered[i], ordered[j]))
                {
                    ordered[i].DuplicateOf = ordered[j].Name;
                    break;
                }
            }
        }

        // Only a flag for the extras list: a film can be clips that also play on their own.
        MarkPlayAll(ordered.Where(p => p.DuplicateOf == null).ToList());

        var candidates = ordered.Where(p => p.DuplicateOf == null && !p.Short).ToList();
        var main = candidates
            .OrderByDescending(p => Math.Round(p.Duration.TotalSeconds))
            .ThenByDescending(p => p.Mpls.Chapters.Count)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (main == null)
        {
            return;
        }

        main.MainFeature = true;
        var mainClips = main.Mpls.Clips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in candidates.Where(p => p != main))
        {
            var shared = other.Mpls.Clips.Count(mainClips.Contains);
            var differs = Math.Abs((other.Duration - main.Duration).TotalSeconds) > 60;
            if (other.Duration >= TimeSpan.FromMinutes(60) && other.Duration >= main.Duration * 0.8 && differs
                && shared * 2 >= mainClips.Count)
            {
                other.PossibleCut = true;
            }
        }

        // Some discs hide the film among dozens of playlists of the same length.
        var nearMain = candidates.Count(p => Math.Abs((p.Duration - main.Duration).TotalSeconds) < 120);
        ManyPlaylists = nearMain >= 8;
    }

    /// <summary>
    /// A playlist made of at least two other playlists, back to back, that fill most of it: the
    /// disc's "play all" for its extras (or episodes). Ripping it would repeat them.
    /// </summary>
    private static void MarkPlayAll(List<PlaylistInfo> playlists)
    {
        foreach (var all in playlists)
        {
            var parts = playlists.Where(p => p != all && p.Duration < all.Duration && Contains(all.Mpls.Items, p.Mpls.Items)).ToList();
            var covered = parts.Sum(p => p.Duration.TotalSeconds);
            if (parts.Count >= 2 && covered >= all.Duration.TotalSeconds * 0.8)
            {
                all.PlayAll = true;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="part"/> plays inside <paramref name="items"/>: the same clips back to
    /// back, each within the matching item's in and out times. TV discs often cut every episode out
    /// of one big clip, which their "play all" plays whole.
    /// </summary>
    private static bool Contains(IReadOnlyList<PlayItem> items, IReadOnlyList<PlayItem> part)
    {
        for (var start = 0; start + part.Count <= items.Count; start++)
        {
            var match = true;
            for (var i = 0; i < part.Count && match; i++)
            {
                var outer = items[start + i];
                match = string.Equals(outer.Clip, part[i].Clip, StringComparison.OrdinalIgnoreCase)
                        && outer.InTime <= part[i].InTime && part[i].OutTime <= outer.OutTime;
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameContent(PlaylistInfo a, PlaylistInfo b)
    {
        // Same clips from the same points: TV discs cut episodes of one length out of one clip.
        return Math.Abs((a.Duration - b.Duration).TotalSeconds) < 1
               && a.Mpls.Clips.SequenceEqual(b.Mpls.Clips, StringComparer.OrdinalIgnoreCase)
               && a.Mpls.Items.Zip(b.Mpls.Items).All(x => Math.Abs((long)x.First.InTime - x.Second.InTime) < Mpls.ClockRate)
               && a.Mpls.Streams.Count == b.Mpls.Streams.Count;
    }
}
