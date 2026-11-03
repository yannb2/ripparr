namespace Ripparr.Disc;

public enum Encryption
{
    /// <summary>Not looked at, or nothing to look at.</summary>
    Unknown,

    /// <summary>The streams read as plain MPEG-TS: nothing to decrypt.</summary>
    None,

    /// <summary>The streams are AACS encrypted: MakeMKV has to decrypt them first.</summary>
    Encrypted,

    /// <summary>Only seen from the outside (an ISO with an AACS folder): treated as encrypted.</summary>
    Likely
}

/// <summary>
/// Tells an AACS encrypted Blu-ray stream from a plain one. A .m2ts file is made of
/// 6144-byte aligned units of 32 packets of 192 bytes. Each packet starts with a
/// 4-byte header whose top two bits (copy permission indicator) are set when the unit
/// is encrypted; only the first 16 bytes of an encrypted unit are readable, so the
/// MPEG-TS sync byte (0x47) of the following packets is lost too.
/// </summary>
public static class EncryptionCheck
{
    public const int AlignedUnit = 6144;
    private const int PacketSize = 192;

    /// <summary>Looks at the first few aligned units of a clip.</summary>
    public static Encryption Check(string m2tsPath, int units = 8)
    {
        try
        {
            using var stream = File.OpenRead(m2tsPath);
            return Check(stream, units);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Encryption.Unknown;
        }
    }

    public static Encryption Check(Stream stream, int units = 8)
    {
        var buffer = new byte[AlignedUnit];
        var seen = 0;
        for (var u = 0; u < units; u++)
        {
            var read = ReadFull(stream, buffer);
            if (read < AlignedUnit)
            {
                break;
            }

            seen++;
            if (IsEncryptedUnit(buffer))
            {
                return Encryption.Encrypted;
            }
        }

        return seen == 0 ? Encryption.Unknown : Encryption.None;
    }

    internal static bool IsEncryptedUnit(ReadOnlySpan<byte> unit)
    {
        if ((unit[0] & 0xC0) != 0)
        {
            return true;
        }

        for (var p = 0; p < AlignedUnit / PacketSize; p++)
        {
            if (unit[p * PacketSize + 4] != 0x47)
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
