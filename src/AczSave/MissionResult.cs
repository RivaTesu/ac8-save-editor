namespace AczSave;

/// One finished campaign sortie, in the units the game stores.
public sealed record MissionResult
{
    public required int Mission { get; init; }
    public required int Aircraft { get; init; }
    public required int Score { get; init; }
    public required int TimeSeconds { get; init; }
    public required int Rank { get; init; }
    public required int GaugeAfter { get; init; }
    public int StyleClass { get; init; } = -1;
    public int AirKills { get; init; }
    public int GroundKills { get; init; }
    public int WingmanAirKills { get; init; }
    public int WingmanGroundKills { get; init; }
    public int Reward { get; init; }
    public IReadOnlyList<int> NewAircraft { get; init; } = [];
    public IReadOnlyList<int> NewSpColors { get; init; } = [];
    public IReadOnlyList<int> Aces { get; init; } = [];
    public IReadOnlyList<int> Medals { get; init; } = [];
    /// Bits 1, 2, 4 grant color schemes 1, 2, 3 on every aircraft; the game sets them only after the final mission.
    public int StyleColors { get; init; }

    public static int ClassOf(int gauge) => gauge < -40 ? 0 : gauge > 40 ? 2 : 1;

    /// Wingman per mission (acz.exe 0x7FF7802DBB00): 0 Pixy, 1 PJ, 2 none.
    public static readonly int[] WingmanByMission = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2];

    /// Port of the campaign branch of acz.exe 0x7FF7801BCD40, checked against an emulated run of it.
    /// Does not advance CurrentMission; the caller does that after the debriefing.
    public void ApplyTo(Campaign c)
    {
        int level = c.FreeMissionLevel;
        int style = Mission == 18 ? 0 : StyleClass >= 0 ? StyleClass : ClassOf(GaugeAfter);
        var rec = c.Records;
        var p = c.Planes;

        for (int row = 0; row <= Campaign.LevelCount; row++) rec.SetClear(row, Mission, style, true);
        if (rec.Rank(level, Mission, style) > Rank) rec.SetRank(level, Mission, style, Rank);
        rec.InsertBest(level, Mission, style, (uint)TimeSeconds, (uint)Score);

        c.Progress.Set(Mission, Aircraft, Score, TimeSeconds, GaugeAfter, Rank, style);
        c.AceStyleGauge = GaugeAfter;
        rec.PlayTimeSeconds += (uint)TimeSeconds;

        p.Money += (uint)Reward;
        p.SetStat(Campaign.Aircraft.StatKills, Aircraft, p.Stat(Campaign.Aircraft.StatKills, Aircraft) + AirKills + GroundKills);
        p.SetStat(Campaign.Aircraft.StatSorties, Aircraft, p.Stat(Campaign.Aircraft.StatSorties, Aircraft) + 1);
        p.SetStat(Campaign.Aircraft.StatAirKills, Aircraft, p.Stat(Campaign.Aircraft.StatAirKills, Aircraft) + AirKills);
        p.SetStat(Campaign.Aircraft.StatGroundKills, Aircraft, p.Stat(Campaign.Aircraft.StatGroundKills, Aircraft) + GroundKills);

        int wingman = Mission < WingmanByMission.Length ? WingmanByMission[Mission] : 2;
        if (wingman <= 1)
        {
            int wac = p.SelectedAircraft(1);
            int shift = wingman;
            Add(p, Campaign.Aircraft.StatPixyAirKills + shift, wac, WingmanAirKills);
            Add(p, Campaign.Aircraft.StatPixyGroundKills + shift, wac, WingmanGroundKills);
            Add(p, Campaign.Aircraft.StatPixyKills + shift, wac, WingmanAirKills + WingmanGroundKills);
            Add(p, Campaign.Aircraft.StatPixySorties + shift, wac, 1);
        }

        foreach (int ac in NewAircraft)
        {
            p.SetUnlocked(ac, 0, true);
            for (int w = 0; w < Campaign.Aircraft.WeaponSlots; w++) p.SetWeaponAvailable(ac, w, true);
            p.SetWeaponOwned(ac, 0, true);
        }
        for (int k = 0; k < 3; k++)
        {
            if ((StyleColors & (1 << k)) == 0) continue;
            for (int ac = 0; ac < Campaign.Aircraft.Count; ac++) { p.SetUnlocked(ac, k + 1, true); p.SetOwned(ac, k + 1, true); }
        }
        foreach (int ac in NewSpColors) { p.SetUnlocked(ac, 4, true); p.SetOwned(ac, 4, true); }
        foreach (int i in Aces) c.Rivals.SetDestroyed(i, true);
        foreach (int i in Medals) c.Rivals.SetMedal(i, true);
    }

    static void Add(Campaign.Aircraft p, int table, int ac, int v) => p.SetStat(table, ac, p.Stat(table, ac) + v);
}
