using Ripparr.Options;

namespace Ripparr.Disc;

public enum SourceKind
{
    /// <summary>A disc image (.iso).</summary>
    Iso,

    /// <summary>A folder holding BDMV (a decrypted backup, or a copied disc).</summary>
    Folder
}

/// <summary>Something a job can start from: an ISO file or a folder with a BDMV folder in it.</summary>
public sealed record DiscSource(string Path, SourceKind Kind, string Root, long Size, DateTime Modified)
{
    /// <summary>The file or folder name, without .iso: usually the release name.</summary>
    public string Name => Kind == SourceKind.Iso
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : System.IO.Path.GetFileName(Path.TrimEnd('/'));

    /// <summary>Where it is under its root, for the list.</summary>
    public string RelativePath => System.IO.Path.GetRelativePath(Root, Path);
}

/// <summary>Finds ISO files and BDMV folders under the source roots.</summary>
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

    /// <summary>The folder that holds BDMV/index.bdmv for a folder source: itself, or itself when it is the BDMV folder's parent.</summary>
    public static string? BdmvParent(string folder)
    {
        if (string.Equals(System.IO.Path.GetFileName(folder.TrimEnd('/')), "BDMV", StringComparison.OrdinalIgnoreCase)
            && FileIn(folder, "index.bdmv") != null)
        {
            return System.IO.Path.GetDirectoryName(folder.TrimEnd('/'));
        }

        var bdmv = DirIn(folder, "BDMV");
        return bdmv != null && FileIn(bdmv, "index.bdmv") != null ? folder : null;
    }

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

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path).TrimEnd('/');
}
