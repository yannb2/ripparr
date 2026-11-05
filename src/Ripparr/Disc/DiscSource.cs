using Ripparr.Options;

namespace Ripparr.Disc;

public enum SourceKind
{
    /// <summary>A disc image (.iso).</summary>
    Iso,

    /// <summary>A folder holding BDMV (a decrypted backup, or a copied disc), or VIDEO_TS (a DVD).</summary>
    Folder
}

/// <summary>Something a job can start from: an ISO file or a folder with a BDMV or VIDEO_TS folder in it.</summary>
public sealed record DiscSource(string Path, SourceKind Kind, string Root, long Size, DateTime Modified)
{
    /// <summary>A DVD folder (VIDEO_TS). An ISO says what it holds only once read: see <see cref="DiscAnalysis.IsDvd"/>.</summary>
    public bool Dvd { get; init; }

    /// <summary>The file or folder name, without .iso: usually the release name.</summary>
    public string Name => Kind == SourceKind.Iso
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : System.IO.Path.GetFileName(Path.TrimEnd('/'));

    /// <summary>Where it is under its root, for the list.</summary>
    public string RelativePath => System.IO.Path.GetRelativePath(Root, Path);
}

/// <summary>Finds ISO files, BDMV folders and DVD (VIDEO_TS) folders under the source roots.</summary>
public static class SourceFinder
{
    public const int MaxDepth = 5;

    /// <param name="skip">Folders never looked into (the work and done folders).</param>
    public static List<DiscSource> Find(IEnumerable<SourceRoot> roots, IEnumerable<string> skip)
    {
        var skipped = skip.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var found = new List<DiscSource>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root.Path))
            {
                Walk(root.Path, root.Path, 0, skipped, found);
            }
        }

        return found.OrderByDescending(s => s.Modified).ToList();
    }

    /// <summary>
    /// The folder that holds a BDMV folder for a folder source: itself, or its parent when it is the
    /// BDMV folder. Many released folders keep index.bdmv only in BDMV/BACKUP, or not at all: a
    /// BDMV folder counts when it has index.bdmv, BACKUP/index.bdmv or a PLAYLIST folder with playlists.
    /// </summary>
    public static string? BdmvParent(string folder)
    {
        if (string.Equals(System.IO.Path.GetFileName(folder.TrimEnd('/')), "BDMV", StringComparison.OrdinalIgnoreCase)
            && IsBdmv(folder))
        {
            return System.IO.Path.GetDirectoryName(folder.TrimEnd('/'));
        }

        var bdmv = DirIn(folder, "BDMV");
        return bdmv != null && IsBdmv(bdmv) ? folder : null;
    }

    /// <summary>A Blu-ray's BDMV folder: index.bdmv there or in BACKUP, or a PLAYLIST folder with .mpls files.</summary>
    public static bool IsBdmv(string bdmv) =>
        FileIn(bdmv, "index.bdmv") != null
        || (DirIn(bdmv, "BACKUP") is { } backup && FileIn(backup, "index.bdmv") != null)
        || (DirIn(bdmv, "PLAYLIST") is { } playlists && HasPlaylists(playlists));

    /// <summary>A disc file in BDMV, or its copy in BDMV/BACKUP when the release left the first one out.</summary>
    public static string? BdmvFile(string bdmv, string name) =>
        FileIn(bdmv, name) ?? (DirIn(bdmv, "BACKUP") is { } backup ? FileIn(backup, name) : null);

    private static bool HasPlaylists(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Any(f => string.Equals(System.IO.Path.GetExtension(f), ".mpls", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The folder that holds VIDEO_TS/VIDEO_TS.IFO (a DVD): itself, or its parent when it is the VIDEO_TS folder.</summary>
    public static string? VideoTsParent(string folder)
    {
        if (string.Equals(System.IO.Path.GetFileName(folder.TrimEnd('/')), "VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            && FileIn(folder, "VIDEO_TS.IFO") != null)
        {
            return System.IO.Path.GetDirectoryName(folder.TrimEnd('/'));
        }

        var videoTs = DirIn(folder, "VIDEO_TS");
        return videoTs != null && FileIn(videoTs, "VIDEO_TS.IFO") != null ? folder : null;
    }

    /// <summary>A disc folder, BDMV or VIDEO_TS, that holds this folder or is it.</summary>
    public static string? DiscParent(string folder) => BdmvParent(folder) ?? VideoTsParent(folder);

    /// <summary>A child folder by name, ignoring case (discs copied from Windows may say "bdmv").</summary>
    public static string? DirIn(string folder, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FileIn(string folder, string name)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Walk(string root, string folder, int depth, HashSet<string> skipped, List<DiscSource> found)
    {
        if (skipped.Contains(Normalize(folder)))
        {
            return;
        }

        var parent = BdmvParent(folder);
        if (parent != null)
        {
            found.Add(FolderSource(root, parent));
            return;
        }

        var dvd = VideoTsParent(folder);
        if (dvd != null)
        {
            found.Add(DvdSource(root, dvd));
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var name = System.IO.Path.GetFileName(entry);
            if (name.StartsWith('.') || name.StartsWith('@') || name.StartsWith('#'))
            {
                continue;
            }

            if (File.Exists(entry))
            {
                if (string.Equals(System.IO.Path.GetExtension(entry), ".iso", StringComparison.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(entry);
                    found.Add(new DiscSource(entry, SourceKind.Iso, root, info.Length, info.LastWriteTimeUtc));
                }
            }
            else if (depth < MaxDepth && Directory.Exists(entry))
            {
                Walk(root, entry, depth + 1, skipped, found);
            }
        }
    }

    private static DiscSource FolderSource(string root, string folder)
    {
        long size = 0;
        var modified = Directory.GetLastWriteTimeUtc(folder);
        var stream = DirIn(DirIn(folder, "BDMV")!, "STREAM");
        if (stream != null)
        {
            foreach (var file in new DirectoryInfo(stream).EnumerateFiles())
            {
                size += file.Length;
                if (file.LastWriteTimeUtc > modified)
                {
                    modified = file.LastWriteTimeUtc;
                }
            }
        }

        return new DiscSource(folder, SourceKind.Folder, root, size, modified);
    }

    private static DiscSource DvdSource(string root, string folder)
    {
        long size = 0;
        var modified = Directory.GetLastWriteTimeUtc(folder);
        foreach (var file in new DirectoryInfo(DirIn(folder, "VIDEO_TS")!).EnumerateFiles())
        {
            size += file.Length;
            if (file.LastWriteTimeUtc > modified)
            {
                modified = file.LastWriteTimeUtc;
            }
        }

        return new DiscSource(folder, SourceKind.Folder, root, size, modified) { Dvd = true };
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path).TrimEnd('/');
}
