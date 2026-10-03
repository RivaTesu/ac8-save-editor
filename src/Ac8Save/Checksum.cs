namespace Ac8Save;

// Recovered from AceCombat8.exe (ULiveSaveGame pack routine):
//   Checksum = FCrc::MemCrc32(PackedData, FCrc::StrCrc32(TEXT("XnMVqmFJnH!2")))
// StrCrc32 feeds each TCHAR as 4 bytes (lo, hi, 0, 0) through the standard reflected CRC-32 table.
public static class Checksum
{
    const string Salt = "XnMVqmFJnH!2";

    static readonly uint[] Table = BuildTable();
    public static readonly uint Seed = StrCrc32(Salt);

    public static bool Known => true;

    public static uint Compute(SaveFile sf, byte[] packed) => MemCrc32(packed, Seed);

    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint MemCrc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    static uint StrCrc32(string s, uint crc = 0)
    {
        crc = ~crc;
        foreach (char ch in s)
        {
            uint c = ch;
            for (int k = 0; k < 4; k++)
            {
                crc = Table[(crc ^ c) & 0xFF] ^ (crc >> 8);
                c >>= 8;
            }
        }
        return ~crc;
    }
}
