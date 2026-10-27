namespace AczSave;

// LZ4 block format (no frame). The game calls LZ4_compress_limitedOutput / LZ4_decompress_safe on the raw buffer.
public static class Lz4
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int outSize)
    {
        var dst = new byte[outSize];
        int ip = 0, op = 0;
        while (ip < src.Length)
        {
            int token = src[ip++];
            int lit = token >> 4;
            if (lit == 15)
            {
                int b;
                do { b = src[ip++]; lit += b; } while (b == 255);
            }
            src.Slice(ip, lit).CopyTo(dst.AsSpan(op));
            ip += lit; op += lit;
            if (ip >= src.Length) break;                       // last literals
            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            int ml = (token & 15) + 4;
            if ((token & 15) == 15)
            {
                int b;
                do { b = src[ip++]; ml += b; } while (b == 255);
            }
            int from = op - offset;
            if (from < 0) throw new InvalidDataException("LZ4: bad offset");
            for (int i = 0; i < ml; i++) dst[op + i] = dst[from + i]; // byte copy handles overlap
            op += ml;
        }
        if (op != outSize) throw new InvalidDataException($"LZ4: expected {outSize} bytes, got {op}");
        return dst;
    }

    public static byte[] Compress(ReadOnlySpan<byte> src)
    {
        const int MinMatch = 4, HashBits = 16, LastLiterals = 5, MfLimit = 12;
        var hash = new int[1 << HashBits];
        Array.Fill(hash, -1);
        var dst = new List<byte>(src.Length / 2 + 64);
        int ip = 0, anchor = 0;
        int limit = src.Length - MfLimit;

        static int H(uint v) => (int)((v * 2654435761u) >> (32 - HashBits));
        static uint Rd32(ReadOnlySpan<byte> s, int i) => (uint)(s[i] | s[i + 1] << 8 | s[i + 2] << 16 | s[i + 3] << 24);

        while (ip < limit)
        {
            uint seq = Rd32(src, ip);
            int h = H(seq);
            int cand = hash[h];
            hash[h] = ip;
            if (cand < 0 || ip - cand > 0xFFFF || Rd32(src, cand) != seq) { ip++; continue; }

            while (ip > anchor && cand > 0 && src[ip - 1] == src[cand - 1]) { ip--; cand--; }
            int litLen = ip - anchor;
            int mlEnd = ip + MinMatch;
            int matchLimit = src.Length - LastLiterals;
            while (mlEnd < matchLimit && src[mlEnd] == src[cand + (mlEnd - ip)]) mlEnd++;
            int ml = mlEnd - ip - MinMatch;

            int tokPos = dst.Count;
            dst.Add(0);
            if (litLen >= 15)
            {
                dst[tokPos] = 15 << 4;
                int rem = litLen - 15;
                for (; rem >= 255; rem -= 255) dst.Add(255);
                dst.Add((byte)rem);
            }
            else dst[tokPos] = (byte)(litLen << 4);
            for (int i = anchor; i < ip; i++) dst.Add(src[i]);
            int off = ip - cand;
            dst.Add((byte)off); dst.Add((byte)(off >> 8));
            if (ml >= 15)
            {
                dst[tokPos] |= 15;
                int rem = ml - 15;
                for (; rem >= 255; rem -= 255) dst.Add(255);
                dst.Add((byte)rem);
            }
            else dst[tokPos] |= (byte)ml;

            ip = mlEnd;
            anchor = ip;
            if (ip < limit) hash[H(Rd32(src, ip - 2))] = ip - 2;
        }
        {
            int litLen = src.Length - anchor;
            int tokPos = dst.Count;
            dst.Add(0);
            if (litLen >= 15)
            {
                dst[tokPos] = 15 << 4;
                int rem = litLen - 15;
                for (; rem >= 255; rem -= 255) dst.Add(255);
                dst.Add((byte)rem);
            }
            else dst[tokPos] = (byte)(litLen << 4);
            for (int i = anchor; i < src.Length; i++) dst.Add(src[i]);
        }
        return dst.ToArray();
    }
}
