using System.Buffers.Binary;

namespace AczSave;

// Offsets: docs/acz-save-format.md. Accessors write into the raw buffer so unknown regions survive round trips.
public sealed class Campaign
{
    public const int AircraftOffset = 332, RivalOffset = 3240, RecordOffset = 6304, CampaignRecordOffset = 27804, UnlockOffset = 28588;
    public const int MissionCount = 19, StyleCount = 3, LevelCount = 6;

    readonly byte[] d; readonly int o;
    public int Index { get; }

    public Campaign(RawSave raw, int slot) { d = raw.Data; o = RawSave.SlotOffset(slot); Index = slot; }

    int I32(int off) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + off));
    uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + off));
    void W32(int off, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + off), v);
    void W32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + off), v);

    /// Empty slots have CurrentMission == 0x13 and the next dword == -1.
    public bool IsEmpty => I32(0) == 0x13 && I32(4) == -1;

    public int CurrentMission { get => I32(0); set => W32(0, value); }
    /// Stage id the briefing picked for the current mission; -1 = not picked yet (branch missions, fresh slot).
    public int Stage { get => I32(4); set => W32(4, value); }
    public int StyleClass { get => I32(8); set => W32(8, value); }
    /// Mission kind from the briefing table: 0 fixed stage, 1 stage by ace style, 2 player-picked branch.
    public int MissionType { get => I32(12); set => W32(12, value); }
    public int AceStyleGauge { get => I32(16); set => W32(16, value); }
    public int FreeMissionLevel { get => I32(148); set => W32(148, value); }

    public Aircraft Planes => new(d, o + AircraftOffset);
    public Record Records => new(d, o + RecordOffset);
    public Rival Rivals => new(d, o + RivalOffset);
    public CampaignRecord Progress => new(d, o + CampaignRecordOffset);
    public Unlock Unlocks => new(d, o + UnlockOffset);

    public readonly struct Aircraft
    {
        public const int Count = 36, ColorSlots = 5, WeaponSlots = 3, StatCount = 12;
        readonly byte[] d; readonly int o;
        public Aircraft(byte[] data, int off) { d = data; o = off; }

        public uint Money { get => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)); set => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), value); }

        public bool Unlocked(int ac, int color = 0) => d[o + 4 + 5 * ac + color] != 0;
        public void SetUnlocked(int ac, int color, bool v) => d[o + 4 + 5 * ac + color] = (byte)(v ? 1 : 0);
        public bool Owned(int ac, int color = 0) => d[o + 292 + 5 * ac + color] != 0;
        public void SetOwned(int ac, int color, bool v) => d[o + 292 + 5 * ac + color] = (byte)(v ? 1 : 0);
        public bool WeaponOwned(int ac, int w) => d[o + 472 + 3 * ac + w] != 0;
        public void SetWeaponOwned(int ac, int w, bool v) => d[o + 472 + 3 * ac + w] = (byte)(v ? 1 : 0);
        public bool WeaponAvailable(int ac, int w) => d[o + 184 + 3 * ac + w] != 0;
        public void SetWeaponAvailable(int ac, int w, bool v) => d[o + 184 + 3 * ac + w] = (byte)(v ? 1 : 0);

        /// Player 1 is the wingman (Pixy, then PJ); his aircraft index keys the wingman stat tables.
        public int SelectedAircraft(int player) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 580 + 4 * player));
        public void SetSelectedAircraft(int player, int ac) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 580 + 4 * player), ac);
        public int SelectedColor(int player) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 588 + 4 * player));
        public void SetSelectedColor(int player, int color) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 588 + 4 * player), color);
        public int SelectedWeapon(int player) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 596 + 4 * player));
        public void SetSelectedWeapon(int player, int w) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 596 + 4 * player), w);

        public const int StatKills = 0, StatPixyKills = 1, StatPjKills = 2, StatSorties = 3, StatPixySorties = 4, StatPjSorties = 5,
            StatAirKills = 6, StatPixyAirKills = 7, StatPjAirKills = 8, StatGroundKills = 9, StatPixyGroundKills = 10, StatPjGroundKills = 11;

        public int Stat(int table, int ac) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 604 + 144 * table + 4 * ac));
        public void SetStat(int table, int ac, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 604 + 144 * table + 4 * ac), v);

        /// Hangar display order -> internal aircraft index (table at acz.exe 0x7ff7802c3a60).
        public static readonly int[] DisplayOrder =
            [15, 0, 31, 28, 4, 29, 16, 10, 8, 21, 30, 22, 20, 24, 5, 17, 18, 1, 11, 3, 32, 6, 12, 14, 9, 23, 2, 25, 19, 7, 27, 26, 13, 33, 35, 34];
    }

    public readonly struct Record
    {
        public const int RankNone = 4;
        readonly byte[] d; readonly int o;
        public Record(byte[] data, int off) { d = data; o = off; }

        public bool Clear(int level, int mission, int style) => d[o + 57 * level + 3 * mission + style] != 0;
        public void SetClear(int level, int mission, int style, bool v) => d[o + 57 * level + 3 * mission + style] = (byte)(v ? 1 : 0);

        int RankOff(int level, int mission, int style) => o + 400 + 4 * (57 * level + 3 * mission + style);
        public int Rank(int level, int mission, int style) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(RankOff(level, mission, style)));
        public void SetRank(int level, int mission, int style, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(RankOff(level, mission, style)), v);

        int EntryOff(int level, int mission, int style, int i) => o + 1996 + 8 * (171 * level + 9 * mission + 3 * style + i);
        public uint Time(int level, int mission, int style, int i = 0) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(EntryOff(level, mission, style, i)));
        public uint Score(int level, int mission, int style, int i = 0) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(EntryOff(level, mission, style, i) + 4));
        public void SetTime(int level, int mission, int style, int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(EntryOff(level, mission, style, i)), v);
        public void SetScore(int level, int mission, int style, int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(EntryOff(level, mission, style, i) + 4), v);

        public uint PlayTimeSeconds { get => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 21152)); set => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + 21152), value); }
        public uint Flag21148 => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 21148));

        /// Inserts into the best-three list for (level, mission, style): time ascending, higher score first on equal time.
        public void InsertBest(int level, int mission, int style, uint time, uint score)
        {
            int at = 0;
            for (; at < 3; at++)
            {
                uint t = Time(level, mission, style, at), s = Score(level, mission, style, at);
                if (t == uint.MaxValue || t > time || (t == time && s < score)) break;
            }
            if (at == 3) return;
            for (int i = 2; i > at; i--)
            {
                SetTime(level, mission, style, i, Time(level, mission, style, i - 1));
                SetScore(level, mission, style, i, Score(level, mission, style, i - 1));
            }
            SetTime(level, mission, style, at, time);
            SetScore(level, mission, style, at, score);
        }
    }

    public readonly struct Rival
    {
        public const int Count = 169, MedalCount = 20;
        readonly byte[] d; readonly int o;
        public Rival(byte[] data, int off) { d = data; o = off; }
        public bool Destroyed(int i) => d[o + i] != 0;
        /// Shooting an ace down also sets its [NEW] marker in Assault Records (second 169-byte array); viewing the entry clears it.
        public void SetDestroyed(int i, bool v) { d[o + i] = (byte)(v ? 1 : 0); d[o + Count + i] = (byte)(v ? 1 : 0); }
        public bool IsNew(int i) => d[o + Count + i] != 0;
        public void SetNew(int i, bool v) => d[o + Count + i] = (byte)(v ? 1 : 0);
        public bool Medal(int i) => d[o + 338 + i] != 0;
        public void SetMedal(int i, bool v) => d[o + 338 + i] = (byte)(v ? 1 : 0);
    }

    /// Per-mission campaign log read by the ending credits; entry 0 holds the starting gauge, entry m + 1 mission m.
    public readonly struct CampaignRecord
    {
        public const int EntrySize = 24, EntryCount = 20;
        readonly byte[] d; readonly int o;
        public CampaignRecord(byte[] data, int off) { d = data; o = off; }

        int E(int mission) => o + EntrySize * (mission + 1);
        int I(int off) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off));
        void W(int off, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(off), v);

        public int StartGauge { get => I(o + 12); set => W(o + 12, value); }
        public int Aircraft(int mission) => I(E(mission));
        public int Score(int mission) => I(E(mission) + 4);
        public int TimeSeconds(int mission) => I(E(mission) + 8);
        public int GaugeAfter(int mission) => I(E(mission) + 12);
        public int Rank(int mission) => I(E(mission) + 16);
        public int Style(int mission) => d[E(mission) + 20];

        public void SetAircraft(int mission, int v) => W(E(mission), v);
        public void SetScore(int mission, int v) => W(E(mission) + 4, v);
        public void SetTimeSeconds(int mission, int v) => W(E(mission) + 8, v);
        public void SetGaugeAfter(int mission, int v) => W(E(mission) + 12, v);
        public void SetRank(int mission, int v) => W(E(mission) + 16, v);
        public void SetStyle(int mission, int v) => d[E(mission) + 20] = (byte)v;

        public void Set(int mission, int aircraft, int score, int timeSeconds, int gaugeAfter, int rank, int style)
        {
            int e = E(mission);
            W(e, aircraft); W(e + 4, score); W(e + 8, timeSeconds); W(e + 12, gaugeAfter); W(e + 16, rank);
            d[e + 20] = (byte)style;
        }
    }

    /// Unlock state of the last mission result (CPropertyUnlock): bit sets over aircraft, aces and medals.
    public readonly struct Unlock
    {
        public const int Size = 41;
        public const int NewAircraft = 0, NewSpColor = 5, AvailableAircraft = 10, Aces = 15, Medals = 37, Flags = 40;
        readonly byte[] d; readonly int o;
        public Unlock(byte[] data, int off) { d = data; o = off; }

        public bool Bit(int field, int i) => (d[o + field + (i >> 3)] & (1 << (i & 7))) != 0;
        public void SetBit(int field, int i, bool v)
        {
            ref byte b = ref d[o + field + (i >> 3)];
            b = (byte)(v ? b | (1 << (i & 7)) : b & ~(1 << (i & 7)));
        }
    }
}
