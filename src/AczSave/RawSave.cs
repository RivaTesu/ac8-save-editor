using System.Buffers.Binary;

namespace AczSave;

// Layout: docs/acz-save-format.md. Unused bytes hold FillByte(i); the game does not check them.
public sealed class RawSave
{
    public const int HeaderSize = 80, SystemSize = 200, SlotSize = 28892, SlotCount = 3;
    public const int SystemOffset = HeaderSize;
    public const int SlotsOffset = HeaderSize + SystemSize;
    public const int ChecksumOffset = SlotsOffset + SlotCount * SlotSize;
    public const int Size = ChecksumOffset + 4;
    public const uint HeaderMagic = 0x12345678;

    public byte[] Data { get; }

    public RawSave(byte[] data)
    {
        if (data.Length != Size) throw new ArgumentException($"raw buffer must be {Size} bytes");
        Data = data;
    }

    public static byte FillByte(int i) => (byte)((-91 * i) ^ (i * (i >> 4) + 79));

    public uint HeaderMagicValue => BinaryPrimitives.ReadUInt32LittleEndian(Data);
    public bool IsValid => HeaderMagicValue == HeaderMagic;

    public static uint ComputeChecksum(ReadOnlySpan<byte> raw)
    {
        uint s = 0;
        for (int i = 0; i < ChecksumOffset; i++) s += (uint)(raw[i] ^ 0xAA);
        return s;
    }
    public uint StoredChecksum => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(ChecksumOffset));
    public bool ChecksumOk => StoredChecksum == ComputeChecksum(Data);
    public void UpdateChecksum() => BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(ChecksumOffset), ComputeChecksum(Data));

    public Span<byte> Header => Data.AsSpan(0, HeaderSize);
    public Span<byte> System => Data.AsSpan(SystemOffset, SystemSize);
    public Span<byte> Slot(int i) => Data.AsSpan(SlotsOffset + i * SlotSize, SlotSize);
    public static int SlotOffset(int i) => SlotsOffset + i * SlotSize;

    public int LastSlot { get => Data[76]; set => Data[76] = (byte)value; }

    public SlotEntry GetSlotEntry(int i) => new(Data, 4 + 24 * i);
    public Campaign GetCampaign(int i) => new(this, i);

    /// Header entry describing a slot on the load screen. Empty slot: Mission == -1.
    public readonly struct SlotEntry
    {
        readonly byte[] d; readonly int o;
        public SlotEntry(byte[] data, int off) { d = data; o = off; }
        int I(int k) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4 * k));
        void W(int k, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 4 * k), v);
        public int Mission { get => I(0); set => W(0, value); }
        public int Level { get => I(1); set => W(1, value); }
        public int Hours { get => I(2); set => W(2, value); }
        public int Minutes { get => I(3); set => W(3, value); }
        public int Seconds { get => I(4); set => W(4, value); }
        public bool Flag { get => d[o + 20] != 0; set => d[o + 20] = (byte)(value ? 1 : 0); }
        public bool IsEmpty => Mission == -1;
        public void SetPlayTime(int seconds) { Hours = seconds / 3600; Minutes = seconds / 60 % 60; Seconds = seconds % 60; }
    }
}
