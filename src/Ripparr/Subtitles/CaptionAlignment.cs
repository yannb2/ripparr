namespace Ripparr.Subtitles;

/// <summary>Lines up the captions of several subtitle tracks by time, for the side-by-side compare.</summary>
public static class CaptionAlignment
{
    /// <summary>One moment of the film: at most one caption per track, null where a track shows nothing.</summary>
    public sealed record Row(TimeSpan Start, PgsCaption?[] Cells);

    /// <summary>
    /// Captions that overlap in time go on the same row, one per track; a caption that starts
    /// after the row has ended, or for a track that already has one on the row, starts a new row.
    /// </summary>
    public static List<Row> Align(IReadOnlyList<IReadOnlyList<PgsCaption>> tracks)
    {
        var events = tracks
            .SelectMany((captions, track) => captions.Select(c => (Track: track, Caption: c)))
            .OrderBy(e => e.Caption.Start)
            .ThenBy(e => e.Track);
        var rows = new List<Row>();
        Row? current = null;
        var end = TimeSpan.Zero;
        foreach (var (track, caption) in events)
        {
            if (current == null || caption.Start >= end || current.Cells[track] != null)
            {
                current = new Row(caption.Start, new PgsCaption?[tracks.Count]);
                rows.Add(current);
                end = caption.End;
            }
            else if (caption.End > end)
            {
                end = caption.End;
            }

            current.Cells[track] = caption;
        }

        return rows;
    }
}
