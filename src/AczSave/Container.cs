using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AczSave;

// Format: docs/acz-save-format.md. The key depends on the Steam account id; TryFindKey recovers it for a save from another account.
public static class Container
{
    public const uint Magic = 0x534758FE;
    const uint K1 = 0xF5C8B683, K2 = 0xBCAF5269, KeyMask = 0x5A7EC3A5;

    public static uint SteamKey(uint accountId)
    {
        ulong sid = 0x0110000100000000UL | accountId;
        return ((uint)sid ^ (uint)(sid >> 32)) & KeyMask;
    }

    static uint Rotr7(uint x) => (x >> 7) | (x << 25);

    public static bool LooksLike(ReadOnlySpan<byte> file) =>
        file.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(file) == Magic;

    /// Decrypts and decompresses save.sav. Returns the raw RawSave.Size buffer.
    public static byte[] Decode(ReadOnlySpan<byte> file, uint accountId, out bool checksumOk) =>
        DecodeWithKey(file, SteamKey(accountId), out checksumOk);

    /// Finds the key a save was encrypted with, without the account id.
    /// The key keeps only the 18 bits of KeyMask, so trying every value and checking the outer checksum is cheap.
    public static bool TryFindKey(ReadOnlySpan<byte> file, out uint key)
    {
        key = 0;
        if (file.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic) return false;
        uint r1 = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        uint d2 = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint d3 = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        uint k = 0;
        do
        {
            int compLen = (int)((d3 ^ d2 ^ r1 ^ k) & 0xFFFFFF);
            if (compLen > 0 && ((compLen + 3) & ~3) + 24 <= file.Length && OuterChecksumOk(file, r1 ^ k, d2, d3, compLen))
            {
                try
                {
                    var raw = DecodeWithKey(file, k, out _);
                    if (new RawSave(raw).IsValid) { key = k; return true; }
                }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
            }
            k = (k - KeyMask) & KeyMask;
        }
        while (k != 0);
        return false;
    }

    static bool OuterChecksumOk(ReadOnlySpan<byte> file, uint d1, uint d2, uint d3, int compLen)
    {
        int aligned = (compLen + 3) & ~3, n = aligned / 4;
        uint r2 = BinaryPrimitives.ReadUInt32LittleEndian(file[(16 + aligned)..]);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(file[(20 + aligned)..]);
        uint add = d1 ^ r2 ^ K1, step = d2 ^ r2 ^ K2;
        uint ck = Rotr7(d3);
        for (int i = 0; i < n; i++) ck += Rotr7(BinaryPrimitives.ReadUInt32LittleEndian(file[(16 + 4 * i)..]));
        return ck == (stored ^ (add + step * (uint)(n + 1)));
    }

    public static byte[] DecodeWithKey(ReadOnlySpan<byte> file, uint key, out bool checksumOk)
    {
        if (file.Length < 20) throw new InvalidDataException("file too short");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (magic != Magic) throw new InvalidDataException($"bad magic 0x{magic:X8}");
        uint d1 = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]) ^ key;
        uint d2 = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint d3 = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        int compLen = (int)((d3 ^ d2 ^ d1) & 0xFFFFFF);
        int aligned = (compLen + 3) & ~3;
        if (file.Length < aligned + 24) throw new InvalidDataException("payload truncated (wrong Steam account id?)");
        uint r2 = BinaryPrimitives.ReadUInt32LittleEndian(file[(16 + aligned)..]);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(file[(20 + aligned)..]);
        uint add = d1 ^ r2 ^ K1, step = d2 ^ r2 ^ K2;
        int n = aligned / 4;

        var plain = new byte[aligned];
        uint ck = Rotr7(d3);
        for (int i = 0; i < n; i++)
        {
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(file[(16 + 4 * i)..]);
            ck += Rotr7(c);
            BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4 * i), c ^ (add + step * (uint)(i + 1)));
        }
        checksumOk = ck == (stored ^ (add + step * (uint)(n + 1)));
        return Lz4.Decompress(plain.AsSpan(0, compLen), RawSave.Size);
    }

    /// Compresses and encrypts a raw buffer into a save.sav image of exactly RawSave.Size bytes.
    public static byte[] Encode(ReadOnlySpan<byte> raw, uint accountId)
    {
        if (raw.Length != RawSave.Size) throw new ArgumentException($"raw must be {RawSave.Size} bytes");
        byte[] comp = Lz4.Compress(raw);
        int compLen = comp.Length;
        int aligned = (compLen + 3) & ~3;
        int total = aligned + 24;
        if (total > RawSave.Size) throw new InvalidDataException("compressed data does not fit");

        var rnd = new byte[16];
        RandomNumberGenerator.Fill(rnd);
        uint r1 = BinaryPrimitives.ReadUInt32LittleEndian(rnd), r2 = BinaryPrimitives.ReadUInt32LittleEndian(rnd.AsSpan(4));
        uint r3 = BinaryPrimitives.ReadUInt32LittleEndian(rnd.AsSpan(8)), r4 = rnd[12];
        uint d1 = r1 ^ SteamKey(accountId);
        uint d3 = d1 ^ r3 ^ ((uint)compLen + (r4 << 24));
        uint add = d1 ^ r2 ^ K1, step = r3 ^ r2 ^ K2;
        int n = aligned / 4;

        var file = new byte[RawSave.Size];
        RandomNumberGenerator.Fill(file.AsSpan(total));                       // tail padding, like the game
        RandomNumberGenerator.Fill(file.AsSpan(16 + compLen, aligned - compLen)); // alignment pad before xor
        comp.CopyTo(file.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(file, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), r1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), r3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), d3);
        uint ck = Rotr7(d3);
        for (int i = 0; i < n; i++)
        {
            var s = file.AsSpan(16 + 4 * i);
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(s) ^ (add + step * (uint)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(s, c);
            ck += Rotr7(c);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16 + aligned), r2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20 + aligned), ck ^ (add + step * (uint)(n + 1)));
        return file;
    }
}
