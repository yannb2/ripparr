namespace Ripparr.Disc;

/// <summary>A playlist with what Ripparr makes of it.</summary>
public sealed class PlaylistInfo
{
    /// <summary>The file name without .mpls ("00800").</summary>
    public required string Name { get; init; }

    public required Mpls Mpls { get; init; }

    /// <summary>
    /// Set when the disc has no playlist for this clip and Ripparr made one from its CLIPINF file
    /// (the film on discs whose BD-J menu builds the playlist at play time). Name is then "clip00294".
    /// </summary>
    public string? FromClip { get; init; }

    /// <summary>The .mpls Ripparr made for <see cref="FromClip"/>; the job writes it into its work folder.</summary>
    public byte[]? MadePlaylist { get; init; }

    /// <summary>A DVD's title (Name is then "title03"): what the IFO says about it.</summary>
    public DvdTitle? Dvd { get; init; }

    /// <summary>A part of a DVD title (some chapters of it, one episode of several): the title's name.</summary>
    public string? PartOf { get; init; }

    /// <summary>For a part: its first and last chapter on the title, from 1.</summary>
    public int FirstChapter { get; init; }

    public int LastChapter { get; init; }

    /// <summary>
    /// A piece of a Blu-ray reel (one deleted scene of a playlist that plays them all): its play
    /// item's number on the reel, from 1. <see cref="PartOf"/> is the reel's name, Name "00050-p03".
    /// </summary>
    public int Piece { get; init; }

    /// <summary>
    /// A reel: three or more short pieces (deleted scenes, outtakes) that no other playlist plays
    /// on its own, one after the other. How many pieces; 0 when it is no reel.
    /// </summary>
    public int Reel { get; set; }

    /// <summary>What the "Reel" badge explains.</summary>
    public string ReelNote =>
        $"{Reel} short pieces back to back (deleted scenes, outtakes...) that do not play on their own: the Extras card can split it into one extra per piece.";

    /// <summary>How the page names it: the playlist number, "Clip 00294", "00050, piece 3", "Title 3" or "Title 1, ch. 7-12".</summary>
    public string Label => FromClip != null ? $"Clip {FromClip}"
        : Piece > 0 ? $"{PartOf}, piece {Piece}"
        : Dvd == null ? Name
        : PartOf == null ? $"Title {Dvd.Number}"
        : $"Title {Dvd.Number}, ch. {FirstChapter}-{LastChapter}";

    public const string ClipPrefix = "clip";

    /// <summary>The .mpls file name without extension: a made playlist is written as the clip's number (00294.mpls), as DGDemux expects.</summary>
    public string FileName => FromClip ?? (Piece > 0 ? PartOf! : Name);

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

    /// <summary>
    /// Far too few bytes for its length (under <see cref="DiscAnalysis.MinimumBitsPerSecond"/>): a
    /// fake that loops a few tiny clips, never the film.
    /// </summary>
    public bool TooLittleData { get; set; }

    /// <summary>
    /// Far more bytes than its length can hold (over <see cref="DiscAnalysis.MaximumBitsPerSecond"/>):
    /// the playlist's times are faked to make it look short, so it is not taken as short.
    /// </summary>
    public bool LengthLooksFaked { get; set; }

    /// <summary>Long, near the film's length, and made of the film's clips in another order: one of a disc's fakes, or the film.</summary>
    public bool Lookalike { get; set; }

    /// <summary>Clips no other long playlist uses (GetMPLS marks these: the film's own scenes are often among them).</summary>
    public int UniqueClips { get; set; }

    /// <summary>How smoothly its clips join, from <see cref="PlaylistFinder"/>: the mean picture change at each join, 0 = seamless.</summary>
    public double? JoinScore { get; set; }

    /// <summary>Why this playlist is the main feature, when something better than its length decided.</summary>
    public string? MainReason { get; set; }

    public double BitsPerSecond => Duration.TotalSeconds > 0 ? Size * 8 / Duration.TotalSeconds : 0;

    public TimeSpan Duration => Mpls.Duration;

    /// <summary>A copy for one job, with its playlist corrected (the shared analysis stays as read).</summary>
    public PlaylistInfo WithMpls(Mpls mpls) => new()
    {
        Name = Name, Mpls = mpls, FromClip = FromClip, MadePlaylist = MadePlaylist, Dvd = Dvd, PartOf = PartOf, FirstChapter = FirstChapter,
        LastChapter = LastChapter, Size = Size, MainFeature = MainFeature, DuplicateOf = DuplicateOf,
        PossibleCut = PossibleCut, Short = Short, PlayAll = PlayAll, TooLittleData = TooLittleData, LengthLooksFaked = LengthLooksFaked,
        Lookalike = Lookalike, UniqueClips = UniqueClips, JoinScore = JoinScore, MainReason = MainReason, Piece = Piece, Reel = Reel
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

    /// <summary>A DVD (VIDEO_TS): its titles are the playlists, MakeMKV rips them, there is no DGDemux step.</summary>
    public bool IsDvd { get; set; }

    public bool HasAacsFolder { get; set; }

    public List<PlaylistInfo> Playlists { get; set; } = [];

    /// <summary>Clip name ("00055") to the size of its .m2ts file.</summary>
    public Dictionary<string, long> ClipSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Many long playlists of nearly the same length: a disc that hides its film among fakes.</summary>
    public bool ManyPlaylists { get; set; }

    /// <summary>
    /// The lowest data rate a film can have: even a DVD-quality Blu-ray runs over 10 Mbit/s. A
    /// playlist under 3 Mbit/s loops tiny clips (one of the fakes some studios add).
    /// </summary>
    public const double MinimumBitsPerSecond = 3_000_000;

    /// <summary>
    /// More than any Blu-ray can play (UHD tops out at 128 Mbit/s for everything together): a
    /// playlist over this, with a few GB in it, has faked times.
    /// </summary>
    public const double MaximumBitsPerSecond = 200_000_000;

    /// <summary>A playlist holding less than this is never "faked long", however short it claims to be.</summary>
    public const long FakedLengthMinimumBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>All the .m2ts files together.</summary>
    public long ClipBytes => ClipSizes.Values.Sum();

    /// <summary>
    /// The playlist taken as the film holds only a sliver of a big disc: the real film's playlist
    /// is missing (made at play time by the disc's BD-J menu) or hidden. MakeMKV can find it.
    /// </summary>
    public bool FilmMissing => MainFeature is { } main && ClipBytes >= 10L * 1024 * 1024 * 1024 && main.Size * 4 < ClipBytes;

    /// <summary>Show "Check clip joins" and "Ask MakeMKV": the disc plays tricks with its playlists.</summary>
    public bool NeedsFinding => HidesFilm || ManyPlaylists || FilmMissing || Playlists.Any(p => p.TooLittleData || p.LengthLooksFaked);

    /// <summary>Several playlists near the film's length use its clips in other orders: the film is hidden among fakes.</summary>
    public bool HidesFilm => Playlists.Count(p => p.Lookalike) >= 3;

    /// <summary>What MakeMKV calls the main feature (it plays the disc's menus, BD-J included), once asked.</summary>
    public string? MakeMkvPick { get; set; }

    /// <summary>What happened when MakeMKV was asked (no answer, an error...).</summary>
    public string? MakeMkvNote { get; set; }

    /// <summary>The playlist whose clips join most smoothly, when it stands clearly apart from the next.</summary>
    public string? JoinPick { get; set; }

    public string? Error { get; set; }

    /// <summary>The episodes in the order the disc's menu shows them (<see cref="MenuReader"/>), once read; null when it could not tell.</summary>
    public List<string>? MenuOrder { get; set; }

    /// <summary>How the menu order was found ("in the order of the disc menu's buttons (clip 00003)"), or why there is none.</summary>
    public string? MenuNote { get; set; }

    /// <summary>The menu order comes from the Java menu's files (a guess), not from the menu's buttons.</summary>
    public bool MenuIsGuess { get; set; }

    /// <summary>The menu was read (or tried) for these playlists, so it is not read again.</summary>
    public string? MenuReadFor { get; set; }

    public List<string> Notes { get; set; } = [];

    public PlaylistInfo? MainFeature => Playlists.FirstOrDefault(p => p.MainFeature);

    public PlaylistInfo? Find(string name) => Playlists.FirstOrDefault(p => p.Name == name) ?? AddPart(name);

    /// <summary>
    /// A part of a DVD title by its name ("title01-c07-12"), made and added to the playlists on
    /// first use: the TV episodes card makes them when a title holds several episodes, and a job
    /// finds its part again on a fresh read of the disc.
    /// </summary>
    public PlaylistInfo? AddPart(string name)
    {
        if (!IsDvd)
        {
            return AddPiece(name);
        }

        if ( DvdDisc.ParsePart(name) is not var (title, from, to)
                   || Playlists.FirstOrDefault(p => p.Name == title) is not { } whole
                   || DvdDisc.Part(whole, from, to) is not { } part)
        {
            return null;
        }

        part.Size = part.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase).Sum(c => ClipSizes.GetValueOrDefault(c));
        lock (Playlists)
        {
            if (Playlists.FirstOrDefault(p => p.Name == name) is { } added)
            {
                return added;
            }

            Playlists.Add(part);
        }

        return part;
    }

    /// <summary>The play items of a reel that count as pieces: those over <see cref="TinyClip"/> (not a logo or a blank between scenes).</summary>
    public static IEnumerable<(PlayItem Item, int Number)> Pieces(PlaylistInfo reel) =>
        reel.Mpls.Items.Select((item, i) => (item, i + 1)).Where(x => x.item.Duration45k > TinyClip.TotalSeconds * Mpls.ClockRate);

    /// <summary>The name of a reel's piece: "00050-p03".</summary>
    public static string PieceName(string reel, int number) => $"{reel}-p{number:00}";

    private static (string Reel, int Number)? ParsePiece(string name)
    {
        var dash = name.LastIndexOf("-p", StringComparison.Ordinal);
        return dash > 0 && int.TryParse(name[(dash + 2)..], out var number) && number > 0 ? (name[..dash], number) : null;
    }

    /// <summary>A piece of a Blu-ray reel by its name ("00050-p03"): its play item cut out as a playlist of its own, made on first use.</summary>
    private PlaylistInfo? AddPiece(string name)
    {
        if (ParsePiece(name) is not var (reelName, number)
            || Playlists.FirstOrDefault(p => p.Name == reelName && p.PartOf == null) is not { Mpls.Raw: { } raw } reel
            || number > reel.Mpls.Items.Count)
        {
            return null;
        }

        byte[] bytes;
        Mpls mpls;
        try
        {
            bytes = MplsCut.Piece(raw, number - 1);
            mpls = Mpls.Parse(bytes);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }

        var piece = new PlaylistInfo
        {
            Name = name, Mpls = mpls, MadePlaylist = bytes, PartOf = reelName, Piece = number,
            Size = mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase).Sum(c => ClipSizes.GetValueOrDefault(c))
        };
        lock (Playlists)
        {
            if (Playlists.FirstOrDefault(p => p.Name == name) is { } added)
            {
                return added;
            }

            Playlists.Add(piece);
        }

        return piece;
    }

    /// <summary>Longest a piece of a reel may run: deleted scenes and outtakes are short.</summary>
    public static readonly TimeSpan ReelPieceMaximum = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Three or more pieces (play items over <see cref="TinyClip"/>), none over
    /// <see cref="ReelPieceMaximum"/>, from at least two clips, not all joined seamlessly (a film's
    /// or featurette's clips are): a reel of deleted scenes or outtakes. Its pieces, or 0.
    /// </summary>
    private static int ReelPieces(PlaylistInfo playlist)
    {
        var items = playlist.Mpls.Items;
        var pieces = Pieces(playlist).Select(x => x.Item).ToList();
        if (pieces.Count < 3 || items.Any(i => i.Duration45k > ReelPieceMaximum.TotalSeconds * Mpls.ClockRate)
            || pieces.Select(i => i.Clip).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2
            || items.Skip(1).All(i => i.ConnectionCondition == 6))
        {
            return 0;
        }

        return pieces.Count;
    }

    /// <summary>
    /// Picks the main feature (the longest, then the one with most chapters), marks playlists
    /// that repeat another one, short ones, and long ones that look like another cut.
    /// </summary>
    public void Classify(TimeSpan minimum)
    {
        // Parts of a DVD title (one episode of several) are not the disc's own playlists: they
        // keep out of the main feature, duplicates and "play all".
        var parts = Playlists.Where(p => p.PartOf != null).ToList();
        if (parts.Count > 0)
        {
            Playlists.RemoveAll(p => p.PartOf != null);
            try
            {
                Classify(minimum);
            }
            finally
            {
                Playlists.AddRange(parts);
            }

            return;
        }

        foreach (var playlist in Playlists)
        {
            playlist.Size = playlist.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase)
                .Sum(c => ClipSizes.GetValueOrDefault(c));
            var allSizes = playlist.Mpls.Clips.All(ClipSizes.ContainsKey);
            playlist.LengthLooksFaked = allSizes && playlist.Size >= FakedLengthMinimumBytes
                                                 && (playlist.Duration.TotalSeconds <= 0 || playlist.BitsPerSecond > MaximumBitsPerSecond);
            playlist.Short = playlist.Duration < minimum && !playlist.LengthLooksFaked;
            playlist.MainFeature = false;
            playlist.PossibleCut = false;
            playlist.DuplicateOf = null;
            playlist.PlayAll = false;
            playlist.Lookalike = false;
            playlist.MainReason = null;
            // Only with every clip's size known: a missing size is not a small one. A DVD's
            // data rate is a fraction of a Blu-ray's: the check is for Blu-ray fakes only.
            playlist.TooLittleData = !IsDvd && !playlist.Short && allSizes
                                     && playlist.BitsPerSecond < MinimumBitsPerSecond;
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

        // The same episode with a second of something tiny before or after it (a logo, a blank):
        // Doctor Who discs list each episode twice like this. The plain one stays.
        foreach (var playlist in ordered.Where(p => p.DuplicateOf == null))
        {
            if (ordered.FirstOrDefault(o => o != playlist && o.DuplicateOf == null && PlusATinyClip(playlist, o)) is { } plain)
            {
                playlist.DuplicateOf = plain.Name;
            }
        }

        // Only a flag for the extras list: a film can be clips that also play on their own.
        MarkPlayAll(ordered.Where(p => p.DuplicateOf == null).ToList());

        // Short pieces back to back that do not play on their own: the extras card can split them.
        foreach (var playlist in Playlists)
        {
            playlist.Reel = IsDvd || playlist.DuplicateOf != null || playlist.PlayAll || playlist.TooLittleData || playlist.FromClip != null
                ? 0
                : ReelPieces(playlist);
        }

        var candidates = ordered.Where(p => p.DuplicateOf == null && !p.Short).ToList();
        foreach (var playlist in candidates)
        {
            playlist.UniqueClips = playlist.Mpls.Clips.Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(c => !candidates.Any(o => o != playlist && o.Mpls.Clips.Contains(c, StringComparer.OrdinalIgnoreCase)));
        }

        // A fake that loops tiny clips is never the film, however long it runs.
        var real = candidates.Where(p => !p.TooLittleData).ToList();
        var pool = real.Count > 0 ? real : candidates;
        // A playlist whose times are faked short but holds gigabytes beats every length.
        var main = pool
            .OrderByDescending(p => p.LengthLooksFaked ? p.Size : 0)
            .ThenByDescending(p => Math.Round(p.Duration.TotalSeconds))
            .ThenByDescending(p => p.Mpls.Chapters.Count)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (main == null)
        {
            return;
        }

        main.MainFeature = true;
        if (candidates.Any(p => p.TooLittleData && p.Duration > main.Duration))
        {
            main.MainReason = "Longer playlists hold too little data for their length: they loop tiny clips.";
        }

        MarkLookalikes(main, pool);
        // What MakeMKV said, or a clear winner on clip joins, beats length.
        // MakeMKV plays the menus, so its pick wins even over a playlist Ripparr thought short or
        // a duplicate (for a duplicate, the playlist listed in its place).
        var makeMkv = Find(MakeMkvPick ?? "");
        if (makeMkv?.DuplicateOf is { } original)
        {
            makeMkv = Find(original);
        }

        var join = Find(JoinPick ?? "");
        var pick = makeMkv ?? (join != null && pool.Contains(join) ? join : null);
        if (makeMkv != null)
        {
            makeMkv.Short = false;
        }

        if (pick != null && pick != main)
        {
            main.MainFeature = false;
            main.MainReason = null;
            main = pick;
            main.MainFeature = true;
        }

        if (main.FromClip != null)
        {
            main.MainReason = "No playlist on the disc plays this clip (the menu builds it at play time): Ripparr made one from its CLIPINF file, without chapters.";
        }

        if (main == makeMkv)
        {
            main.MainReason = "MakeMKV calls it the main feature.";
        }
        else if (main.Name == JoinPick)
        {
            main.MainReason = "Its clips join most smoothly of the lookalikes.";
        }

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
    /// Playlists near the main one's length made mostly of the same clips in another order: on
    /// discs that hide the film, all but one of these are fakes (and the main one may be a fake too).
    /// </summary>
    private static void MarkLookalikes(PlaylistInfo main, List<PlaylistInfo> pool)
    {
        var mainClips = main.Mpls.Clips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lookalikes = pool.Where(p => p != main
                                         && Math.Abs((p.Duration - main.Duration).TotalSeconds) < 120
                                         && p.Mpls.Clips.Count >= 2
                                         && p.Mpls.Clips.Count(mainClips.Contains) * 10 >= mainClips.Count * 8
                                         && !p.Mpls.Clips.SequenceEqual(main.Mpls.Clips, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (lookalikes.Count == 0)
        {
            return;
        }

        main.Lookalike = true;
        foreach (var playlist in lookalikes)
        {
            playlist.Lookalike = true;
        }
    }

    /// <summary>
    /// A playlist made of at least two other playlists, back to back, that fill most of it: the
    /// disc's "play all" for its extras (or episodes). Ripping it would repeat them.
    /// </summary>
    private static void MarkPlayAll(List<PlaylistInfo> playlists)
    {
        foreach (var all in playlists)
        {
            // An episode with a logo of its own the "play all" leaves out counts by its longest clip.
            var parts = playlists.Where(p => p != all && p.Duration < all.Duration && EpisodeFinder.StartIn(all.Mpls.Items, p.Mpls.Items) != null).ToList();
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

    /// <summary>Longest a clip added to a playlist may run for the two to count as the same.</summary>
    public static readonly TimeSpan TinyClip = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <paramref name="longer"/> plays all of <paramref name="plain"/>'s items, in order and from the
    /// same points, plus items of <see cref="TinyClip"/> or less in all.
    /// </summary>
    private static bool PlusATinyClip(PlaylistInfo longer, PlaylistInfo plain)
    {
        var big = longer.Mpls.Items;
        var small = plain.Mpls.Items;
        if (big.Count <= small.Count || small.Count == 0 || longer.Mpls.Streams.Count != plain.Mpls.Streams.Count
            || longer.Duration - plain.Duration > TinyClip || longer.Duration < plain.Duration)
        {
            return false;
        }

        for (var start = 0; start + small.Count <= big.Count; start++)
        {
            if (small.Select((item, i) => (item, other: big[start + i])).All(x =>
                    string.Equals(x.item.Clip, x.other.Clip, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((long)x.item.InTime - x.other.InTime) < Mpls.ClockRate
                    && Math.Abs((long)x.item.OutTime - x.other.OutTime) < Mpls.ClockRate))
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
