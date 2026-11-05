using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class BdmvFolderTests
{
    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    [TestMethod]
    public void FindsBdmvFoldersWithoutIndexBdmv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ripparr-bdmv-{Guid.NewGuid():N}");
        // As released: index.bdmv in BDMV, only in BACKUP (Rocky, Monsters University), or nowhere.
        Touch(Path.Combine(root, "Full", "BDMV", "index.bdmv"));
        Touch(Path.Combine(root, "Backup.Only", "BDMV", "BACKUP", "index.bdmv"));
        Touch(Path.Combine(root, "Backup.Only", "BDMV", "BACKUP", "MovieObject.bdmv"));
        Touch(Path.Combine(root, "Playlists.Only", "BDMV", "PLAYLIST", "00800.mpls"));
        Directory.CreateDirectory(Path.Combine(root, "Empty", "BDMV", "PLAYLIST"));
        try
        {
            var found = SourceFinder.Find([new Options.SourceRoot { Path = root }], []).Select(s => Path.GetFileName(s.Path)).Order().ToList();

            CollectionAssert.AreEqual(new[] { "Backup.Only", "Full", "Playlists.Only" }, found);
            var backup = Path.Combine(root, "Backup.Only", "BDMV");
            Assert.AreEqual(Path.Combine(backup, "BACKUP", "MovieObject.bdmv"), SourceFinder.BdmvFile(backup, "MovieObject.bdmv"));
            Assert.AreEqual(Path.Combine(root, "Backup.Only"), SourceFinder.BdmvParent(backup));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
