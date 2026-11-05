namespace Ripparr.Disc;

/// <summary>
/// Sorting for the Sources list. Every column sorts both ways; a column you
/// click first sorts its natural way (size and Changed biggest or newest
/// first, the rest A to Z). Ties go by name. Same behaviour as Scratcharr.
/// </summary>
public static class SourceSort
{
    public const string Name = "name";
    public const string Kind = "kind";
    public const string Size = "size";
    public const string Changed = "changed";
    public const string Where = "where";

    /// <summary>The list as it opens: newest first.</summary>
    public const string Default = Changed;

    public static readonly string[] Columns = [Name, Kind, Size, Changed, Where];

    /// <summary>A known column, or the default for anything else (an old or hand-typed link).</summary>
    public static string Column(string? sort) =>
        Columns.FirstOrDefault(c => string.Equals(c, sort?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>How a column sorts when you first click it.</summary>
    public static bool StartsDescending(string column) => column is Size or Changed;

    /// <summary>The direction from the URL ("asc" or "desc"), or the column's own when there is none.</summary>
    public static bool Descending(string column, string? dir) => dir?.Trim().ToLowerInvariant() switch
    {
        "asc" => false,
        "desc" => true,
        _ => StartsDescending(column)
    };

    /// <summary>Clicking a header: the sorted column flips, another column starts its own way.</summary>
    public static (string Column, bool Descending) Click(string current, bool descending, string clicked) =>
        clicked == current ? (current, !descending) : (clicked, StartsDescending(clicked));

    public static IEnumerable<DiscSource> Apply(IEnumerable<DiscSource> sources, string column, bool descending)
    {
        var ordered = column switch
        {
            Kind => By(sources, KindLabel, descending, StringComparer.OrdinalIgnoreCase),
            Size => By(sources, s => s.Size, descending, Comparer<long>.Default),
            Changed => By(sources, s => s.Modified, descending, Comparer<DateTime>.Default),
            Where => By(sources, WhereText, descending, StringComparer.OrdinalIgnoreCase),
            _ => By(sources, s => s.Name, descending, StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Path, StringComparer.Ordinal);
    }

    /// <summary>The folder shown in the Where column.</summary>
    public static string WhereText(DiscSource source) =>
        $"{source.Root}/{Path.GetDirectoryName(source.RelativePath)}";

    public static string KindLabel(SourceKind kind) => kind == SourceKind.Iso ? "ISO" : "BDMV";

    /// <summary>ISO, BDMV or DVD (a VIDEO_TS folder), as the Kind column shows it.</summary>
    public static string KindLabel(DiscSource source) => source.Kind == SourceKind.Folder && source.Dvd ? "DVD" : KindLabel(source.Kind);

    private static IOrderedEnumerable<DiscSource> By<T>(IEnumerable<DiscSource> sources, Func<DiscSource, T> key,
        bool descending, IComparer<T> comparer) =>
        descending ? sources.OrderByDescending(key, comparer) : sources.OrderBy(key, comparer);
}
