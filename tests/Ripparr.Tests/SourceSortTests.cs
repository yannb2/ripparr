using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class SourceSortTests
{
    private static readonly DiscSource Heat = new("/downloads/discs/Heat.1995.iso", SourceKind.Iso, "/downloads", 80_000, new DateTime(2026, 11, 1));
    private static readonly DiscSource Alien = new("/mnt/pc/usb01/Alien.1979", SourceKind.Folder, "/mnt/pc", 60_000, new DateTime(2026, 11, 3));
    private static readonly DiscSource Brazil = new("/downloads/Brazil.1985.iso", SourceKind.Iso, "/downloads", 60_000, new DateTime(2026, 10, 20));
    private static readonly DiscSource[] All = [Heat, Alien, Brazil];

    private static string[] Names(string column, bool descending) =>
        SourceSort.Apply(All, column, descending).Select(s => s.Name).ToArray();

    [TestMethod]
    public void EveryColumnSortsBothWays()
    {
        CollectionAssert.AreEqual(new[] { "Alien.1979", "Brazil.1985", "Heat.1995" }, Names(SourceSort.Name, false));
        CollectionAssert.AreEqual(new[] { "Heat.1995", "Brazil.1985", "Alien.1979" }, Names(SourceSort.Name, true));
        CollectionAssert.AreEqual(new[] { "Alien.1979", "Heat.1995", "Brazil.1985" }, Names(SourceSort.Changed, true));
        CollectionAssert.AreEqual(new[] { "Brazil.1985", "Heat.1995", "Alien.1979" }, Names(SourceSort.Changed, false));
        CollectionAssert.AreEqual(new[] { "Alien.1979", "Brazil.1985", "Heat.1995" }, Names(SourceSort.Kind, false), "BDMV before ISO, ties by name");
        CollectionAssert.AreEqual(new[] { "Brazil.1985", "Heat.1995", "Alien.1979" }, Names(SourceSort.Kind, true));
        CollectionAssert.AreEqual(new[] { "Brazil.1985", "Heat.1995", "Alien.1979" }, Names(SourceSort.Where, false), "/downloads/. < /downloads/discs < /mnt/pc/usb01");
        CollectionAssert.AreEqual(new[] { "Alien.1979", "Heat.1995", "Brazil.1985" }, Names(SourceSort.Where, true));
    }

    [TestMethod]
    public void EqualSizesFallBackToTheName()
    {
        CollectionAssert.AreEqual(new[] { "Heat.1995", "Alien.1979", "Brazil.1985" }, Names(SourceSort.Size, true));
        CollectionAssert.AreEqual(new[] { "Alien.1979", "Brazil.1985", "Heat.1995" }, Names(SourceSort.Size, false));
    }

    [TestMethod]
    public void ClickFlipsTheSortedColumnAndStartsOthersTheirOwnWay()
    {
        Assert.AreEqual((SourceSort.Changed, false), SourceSort.Click(SourceSort.Changed, true, SourceSort.Changed));
        Assert.AreEqual((SourceSort.Name, false), SourceSort.Click(SourceSort.Changed, true, SourceSort.Name));
        Assert.AreEqual((SourceSort.Size, true), SourceSort.Click(SourceSort.Name, false, SourceSort.Size));
    }

    [TestMethod]
    public void UrlValuesAreReadSafely()
    {
        Assert.AreEqual(SourceSort.Changed, SourceSort.Column(null));
        Assert.AreEqual(SourceSort.Changed, SourceSort.Column("bogus"));
        Assert.AreEqual(SourceSort.Size, SourceSort.Column("SIZE"));
        Assert.IsTrue(SourceSort.Descending(SourceSort.Changed, null), "the list opens newest first");
        Assert.IsFalse(SourceSort.Descending(SourceSort.Name, null));
        Assert.IsFalse(SourceSort.Descending(SourceSort.Size, "asc"));
        Assert.IsTrue(SourceSort.Descending(SourceSort.Name, "desc"));
    }
}
