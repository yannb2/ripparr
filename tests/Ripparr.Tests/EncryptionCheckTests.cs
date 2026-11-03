using Ripparr.Disc;

namespace Ripparr.Tests;

[TestClass]
public class EncryptionCheckTests
{
    private static byte[] PlainUnits(int count)
    {
        var data = new byte[EncryptionCheck.AlignedUnit * count];
        for (var p = 0; p < data.Length / 192; p++)
        {
            data[p * 192 + 4] = 0x47;
        }

        return data;
    }

    [TestMethod]
    public void PlainStreamsAreNotEncrypted()
    {
        Assert.AreEqual(Encryption.None, EncryptionCheck.Check(new MemoryStream(PlainUnits(4))));
    }

    [TestMethod]
    public void CopyPermissionBitsMeanEncrypted()
    {
        var data = PlainUnits(4);
        data[EncryptionCheck.AlignedUnit] |= 0xC0;
        Assert.AreEqual(Encryption.Encrypted, EncryptionCheck.Check(new MemoryStream(data)));
    }

    [TestMethod]
    public void LostSyncBytesMeanEncrypted()
    {
        var data = PlainUnits(2);
        data[192 * 3 + 4] = 0x12;
        Assert.AreEqual(Encryption.Encrypted, EncryptionCheck.Check(new MemoryStream(data)));
    }

    [TestMethod]
    public void TooShortIsUnknown()
    {
        Assert.AreEqual(Encryption.Unknown, EncryptionCheck.Check(new MemoryStream(new byte[100])));
    }
}
