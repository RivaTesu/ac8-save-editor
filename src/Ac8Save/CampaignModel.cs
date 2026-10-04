namespace Ac8Save;

// Typed view over the Campaign.sav property tree. Every edit mutates the tree so Writer.Save keeps unknown data intact.
public sealed class CampaignModel
{
    public SaveFile File { get; }
    public CampaignModel(SaveFile file) { File = file; }

    Property? Find(string name) => File.AllProps.FirstOrDefault(p => p.Name == name);

    public ArrayValue? Array(string name) => Find(name)?.Value as ArrayValue;

    public List<uint> U32List(string name)
    {
        var a = Array(name);
        if (a == null) return new();
        return a.Items.Select(e => (uint)((IntValue)e.Value).V).ToList();
    }

    public bool Contains(string name, uint id) => Array(name)?.Items.Any(e => e.Value is IntValue iv && (uint)iv.V == id) == true;

    public void SetContains(string name, uint id, bool present, string? mirrorNewly = null)
    {
        var a = Array(name);
        if (a == null) return;
        var idx = a.Items.FindIndex(e => e.Value is IntValue iv && (uint)iv.V == id);
        if (present && idx < 0) AddU32(a, id);
        else if (!present && idx >= 0) { a.Items.RemoveAt(idx); Reindex(a); }
        if (mirrorNewly != null && present)
        {
            var n = Array(mirrorNewly);
            if (n != null && !n.Items.Any(e => e.Value is IntValue iv && (uint)iv.V == id)) AddU32(n, id);
        }
        if (mirrorNewly != null && !present)
        {
            var n = Array(mirrorNewly);
            if (n != null) { n.Items.RemoveAll(e => e.Value is IntValue iv && (uint)iv.V == id); Reindex(n); }
        }
    }

    static void AddU32(ArrayValue a, uint id)
    {
        var e = new Element { Index = a.Items.Count, Parent = a };
        e.Value = new IntValue { V = id, Kind = a.ElemType.Name, Parent = e };
        a.Items.Add(e);
    }

    static void Reindex(ArrayValue a) { for (int i = 0; i < a.Items.Count; i++) a.Items[i].Index = i; }

    public long Int(string name) => Find(name)?.Value is IntValue iv ? iv.V : 0;
    public void SetInt(string name, long v) { if (Find(name)?.Value is IntValue iv) iv.V = v; }
    public string EnumOf(string name) => Find(name)?.Value is EnumValue ev ? ev.V : "";
    public void SetEnum(string name, string v) { if (Find(name)?.Value is EnumValue ev) ev.V = v; }

    // OwnedAircrafts: TMap<uint32 PlaneID, uint8 copies>. The tree sells up to 4 copies (player + 3 wingmen).
    // An aircraft that the game grants as a reward gets 4.
    public Dictionary<uint, byte> OwnedAircraft()
    {
        var d = new Dictionary<uint, byte>();
        if (Find("OwnedAircrafts")?.Value is MapValue m)
            foreach (var e in m.Items) d[(uint)((IntValue)e.Key).V] = ((ByteValue)e.Val).V;
        return d;
    }

    public void SetOwnedAircraft(uint id, bool owned, byte copies = 4)
    {
        if (Find("OwnedAircrafts")?.Value is not MapValue m) return;
        var idx = m.Items.FindIndex(e => (uint)((IntValue)e.Key).V == id);
        if (owned && idx < 0)
        {
            var e = new MapEntry { Index = m.Items.Count, Parent = m };
            e.Key = new IntValue { V = id, Kind = "UInt32Property", Parent = e };
            e.Val = new ByteValue { V = copies, Parent = e };
            m.Items.Add(e);
        }
        else if (!owned && idx >= 0)
        {
            m.Items.RemoveAt(idx);
            for (int i = 0; i < m.Items.Count; i++) m.Items[i].Index = i;
        }
        SetContains("NewlyOwnedAircrafts", id, owned);
    }

    // The game keeps owned special weapons in AircraftTypeRecords[].OwnedWeapons, not in the OwnedAircrafts value.
    // Without a record the hangar shows every special weapon as locked, including the default one.
    StructValue? AircraftRecord(uint planeId) =>
        Array("AircraftTypeRecords")?.Items.Select(e => e.Value as StructValue)
            .FirstOrDefault(sv => sv?.Props.FirstOrDefault(p => p.Name == "PlaneID")?.Value is IntValue iv && (uint)iv.V == planeId);

    public List<string> OwnedWeapons(uint planeId) =>
        AircraftRecord(planeId)?.Props.FirstOrDefault(p => p.Name == "OwnedWeapons")?.Value is ArrayValue a
            ? a.Items.Select(e => (e.Value as EnumValue)?.V ?? "").ToList()
            : new();

    // Creates an empty record the same way the game does when it grants an aircraft: zero stats, no pilot data, no weapons.
    StructValue? EnsureAircraftRecord(uint planeId, string category)
    {
        if (AircraftRecord(planeId) is StructValue found) return found;
        if (Array("AircraftTypeRecords") is not ArrayValue list || list.Items.Count == 0 || list.Items[0].Value is not StructValue template) return null;
        var w = new Writer();
        w.Properties(template.Props);
        var r = new Reader(w.ToArray());
        var copy = new StructValue();
        copy.Props = r.Properties(copy);
        foreach (var p in copy.Props)
        {
            switch (p.Value)
            {
                case IntValue iv: iv.V = p.Name == "PlaneID" ? planeId : 0; break;
                case EnumValue ev when p.Name == "PlaneCategory" && category != "": ev.V = category; break;
                case ArrayValue av: av.Items.Clear(); break;
            }
        }
        var el = new Element { Index = list.Items.Count, Value = copy, Parent = list };
        copy.Parent = el;
        list.Items.Add(el);
        return copy;
    }

    // order: ELiveWeaponID names by numeric value. The game keeps both weapon lists sorted that way.
    public bool SetOwnedWeapon(uint planeId, string weaponId, bool owned, string category = "", List<string>? order = null)
    {
        var rec = owned ? EnsureAircraftRecord(planeId, category) : AircraftRecord(planeId);
        if (rec == null) return !owned;
        int Rank(string id) { var i = order?.IndexOf(id) ?? -1; return i < 0 ? int.MaxValue : i; }
        foreach (var name in new[] { "OwnedWeapons", "NewlyOwnedWeapons" })
        {
            if (rec.Props.FirstOrDefault(p => p.Name == name)?.Value is not ArrayValue a) continue;
            var idx = a.Items.FindIndex(e => e.Value is EnumValue ev && ev.V == weaponId);
            if (owned && idx < 0)
            {
                var at = a.Items.FindIndex(e => e.Value is EnumValue ev && Rank(ev.V) > Rank(weaponId));
                if (at < 0) at = a.Items.Count;
                var e = new Element { Parent = a };
                e.Value = new EnumValue { V = weaponId, Parent = e };
                a.Items.Insert(at, e);
                Reindex(a);
            }
            else if (!owned && idx >= 0) { a.Items.RemoveAt(idx); Reindex(a); }
        }
        return true;
    }

    public uint FeatureFlagMask { get => (uint)Int("FeatureFlagMask"); set => SetInt("FeatureFlagMask", value); }

    public bool HasFeature(int bit) => (FeatureFlagMask & (1u << bit)) != 0;
    public void SetFeature(int bit, bool on) => FeatureFlagMask = on ? FeatureFlagMask | (1u << bit) : FeatureFlagMask & ~(1u << bit);

    public IEnumerable<UnlockEntry> UnlockEntries()
    {
        if (Array("UnlockData") is not ArrayValue a) yield break;
        foreach (var el in a.Items)
        {
            if (el.Value is not StructValue sv) continue;
            var id = sv.Props.FirstOrDefault(x => x.Name == "ID")?.Value as IntValue;
            var act = sv.Props.FirstOrDefault(x => x.Name == "bIsActivated")?.Value as BoolValue;
            var type = sv.Props.FirstOrDefault(x => x.Name == "ActionType")?.Value as EnumValue;
            if (id == null || act == null) continue;
            yield return new UnlockEntry { Id = (uint)id.V, Activated = act, ActionType = type?.V ?? "" };
        }
    }

    public List<MissionRecord> Missions()
    {
        var list = new List<MissionRecord>();
        if (Array("CompletedMissionList") is not ArrayValue a) return list;
        foreach (var el in a.Items)
        {
            if (el.Value is not StructValue sv) continue;
            var rec = new MissionRecord { Struct = sv };
            rec.MissionId = (int)((sv.Props.FirstOrDefault(x => x.Name == "MissionID")?.Value as IntValue)?.V ?? 0);
            rec.LastRank = sv.Props.FirstOrDefault(x => x.Name == "LastRank")?.Value as EnumValue;
            if (sv.Props.FirstOrDefault(x => x.Name == "DifficultyList")?.Value is ArrayValue dl)
                foreach (var d in dl.Items)
                {
                    if (d.Value is not StructValue ds) continue;
                    var dr = new DifficultyRecord
                    {
                        Level = ds.Props.FirstOrDefault(x => x.Name == "DifficultyLevel")?.Value as ByteValue,
                        HighestRank = ds.Props.FirstOrDefault(x => x.Name == "HighestRank")?.Value as EnumValue,
                        SortieCount = ds.Props.FirstOrDefault(x => x.Name == "SortieCount")?.Value as IntValue,
                    };
                    if (ds.Props.FirstOrDefault(x => x.Name == "RecordList")?.Value is ArrayValue rl && rl.Items.Count > 0 && rl.Items[0].Value is StructValue rs)
                    {
                        dr.TimeMs = rs.Props.FirstOrDefault(x => x.Name == "CompletionTimeMilli")?.Value as IntValue;
                        dr.Score = rs.Props.FirstOrDefault(x => x.Name == "CompletionScore")?.Value as IntValue;
                    }
                    rec.Difficulties.Add(dr);
                }
            list.Add(rec);
        }
        return list;
    }

    // Adds a difficulty entry to a mission by cloning an existing one and changing the level.
    public DifficultyRecord? AddDifficulty(MissionRecord rec, byte level, string rank = "ELiveClearRank::S")
    {
        if (rec.Struct.Props.FirstOrDefault(x => x.Name == "DifficultyList")?.Value is not ArrayValue dl || dl.Items.Count == 0) return null;
        if (dl.Items[0].Value is not StructValue template) return null;
        var w = new Writer();
        w.Properties(template.Props);
        var r = new Reader(w.ToArray());
        var copy = new StructValue();
        copy.Props = r.Properties(copy);
        var el = new Element { Index = dl.Items.Count, Value = copy, Parent = dl };
        copy.Parent = el;
        dl.Items.Add(el);
        if (copy.Props.FirstOrDefault(x => x.Name == "DifficultyLevel")?.Value is ByteValue bv) bv.V = level;
        if (copy.Props.FirstOrDefault(x => x.Name == "HighestRank")?.Value is EnumValue ev) ev.V = rank;
        if (copy.Props.FirstOrDefault(x => x.Name == "SortieCount")?.Value is IntValue sc) sc.V = 1;
        return Missions().First(m => m.Struct == rec.Struct).Difficulties.Last();
    }

    public void RemoveDifficulty(MissionRecord rec, DifficultyRecord d)
    {
        if (rec.Struct.Props.FirstOrDefault(x => x.Name == "DifficultyList")?.Value is not ArrayValue dl) return;
        var idx = dl.Items.FindIndex(e => e.Value is StructValue sv && sv.Props.Any(p => p.Value == d.Level));
        if (idx >= 0) { dl.Items.RemoveAt(idx); for (int i = 0; i < dl.Items.Count; i++) dl.Items[i].Index = i; }
    }

    public HashSet<string> MenuMiscFlags()
    {
        var set = new HashSet<string>();
        if (Find("MenuMiscFlags")?.Value is Ac8Save.SetValue s)
            foreach (var e in s.Items) if (e.Value is EnumValue ev) set.Add(ev.V);
        return set;
    }

    public void SetMenuMiscFlag(string flag, bool on)
    {
        if (Find("MenuMiscFlags")?.Value is not Ac8Save.SetValue s) return;
        var idx = s.Items.FindIndex(e => e.Value is EnumValue ev && ev.V == flag);
        if (on && idx < 0)
        {
            var e = new Element { Index = s.Items.Count, Parent = s };
            e.Value = new EnumValue { V = flag, Parent = e };
            s.Items.Add(e);
        }
        else if (!on && idx >= 0) { s.Items.RemoveAt(idx); for (int i = 0; i < s.Items.Count; i++) s.Items[i].Index = i; }
    }
}

public sealed class UnlockEntry { public uint Id; public BoolValue Activated = null!; public string ActionType = ""; }
public sealed class MissionRecord { public StructValue Struct = null!; public int MissionId; public EnumValue? LastRank; public List<DifficultyRecord> Difficulties = new(); }
public sealed class DifficultyRecord { public ByteValue? Level; public EnumValue? HighestRank; public IntValue? SortieCount; public IntValue? TimeMs; public IntValue? Score; }
