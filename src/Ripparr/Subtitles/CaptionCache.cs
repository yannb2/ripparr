namespace Ripparr.Subtitles;

/// <summary>
/// The captions of the last few .sup files the preview opened, so a page of 50 pictures reads
/// the file once. Keyed by path and modification time.
/// </summary>
public static class CaptionCache
{
    private const int Size = 4;
    private static readonly object Lock = new();
    private static readonly LinkedList<(string Key, List<PgsCaption> Captions)> Entries = new();

    public static List<PgsCaption> Get(string path)
    {
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        lock (Lock)
        {
            for (var node = Entries.First; node != null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    Entries.Remove(node);
                    Entries.AddFirst(node);
                    return node.Value.Captions;
                }
            }
        }

        var captions = PgsReader.Read(path);
        lock (Lock)
        {
            Entries.AddFirst((key, captions));
            while (Entries.Count > Size)
            {
                Entries.RemoveLast();
            }
        }

        return captions;
    }
}
