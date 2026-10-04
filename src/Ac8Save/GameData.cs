using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ac8Save;

// Asset access behind one interface so the app can ship a folder (dev) or one assets.zip (release).
public sealed class AssetStore : IDisposable
{
    readonly string? dir;
    readonly ZipArchive? zip;
    readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    public AssetStore(string root)
    {
        var zipPath = root.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? root : Path.Combine(Path.GetDirectoryName(root) ?? root, "assets.zip");
        if (File.Exists(zipPath))
        {
            zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries) if (e.Length > 0) entries[e.FullName.Replace('\\', '/')] = e;
        }
        else if (Directory.Exists(root)) dir = root;
    }

    public bool Available => zip != null || dir != null;

    public bool Exists(string rel) => zip != null ? entries.ContainsKey(rel) : dir != null && File.Exists(Path.Combine(dir, rel));

    public byte[]? ReadBytes(string rel)
    {
        if (zip != null)
        {
            if (!entries.TryGetValue(rel, out var e)) return null;
            lock (zip)
            {
                using var s = e.Open();
                using var ms = new MemoryStream((int)e.Length);
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        if (dir == null) return null;
        var p = Path.Combine(dir, rel);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    public string? ReadText(string rel)
    {
        var b = ReadBytes(rel);
        return b == null ? null : System.Text.Encoding.UTF8.GetString(b);
    }

    // Relative paths under a folder, e.g. "parts".
    public IEnumerable<string> List(string folder)
    {
        var pre = folder.TrimEnd('/') + "/";
        if (zip != null) return entries.Keys.Where(k => k.StartsWith(pre, StringComparison.OrdinalIgnoreCase)).ToList();
        if (dir == null) return Enumerable.Empty<string>();
        var d = Path.Combine(dir, folder);
        return Directory.Exists(d) ? Directory.GetFiles(d).Select(f => pre + Path.GetFileName(f)).ToList() : Enumerable.Empty<string>();
    }

    public void Dispose() => zip?.Dispose();
}

// Static game data exported from the paks: DataTables as JSON, decrypted text tables, PNG icons.
public sealed class GameData
{
    public AssetStore Store { get; }
    public Dictionary<string, string> Text { get; private set; } = new();
    public Dictionary<string, string> TextEn { get; private set; } = new();
    public List<MedalInfo> Medals { get; } = new();
    public List<AircraftInfo> Aircraft { get; } = new();
    public List<SkinInfo> Skins { get; } = new();
    public List<EmblemInfo> Emblems { get; } = new();
    public List<PartInfo> Parts { get; } = new();
    public List<MissionInfo> Missions { get; } = new();
    public List<AssaultInfo> AssaultRecords { get; } = new();
    public List<TreeNodeInfo> TreeNodes { get; } = new();
    public Dictionary<uint, UnlockRule> UnlockRules { get; } = new();
    // Key is the full enum name, e.g. "ELiveWeaponID::WID_4aam".
    public Dictionary<string, WeaponInfo> Weapons { get; } = new();
    // ELiveWeaponID names in numeric order.
    public List<string> WeaponOrder { get; } = new();

    public GameData(string root, string lang = "en")
    {
        Store = new AssetStore(root);
        if (!Store.Available) return;
        Text = LoadText(lang == "pt" ? "data/text_CP_K.json" : "data/text_CP_B.json");
        TextEn = LoadText("data/text_CP_B.json");
        LoadMedals("data/medal.json");
        LoadAircraft("data/aircraft.json");
        LoadSkins("data/skin.json");
        LoadEmblems("data/emblem.json");
        LoadParts("data/parts.json");
        LoadMissions("data/mission.json");
        LoadAssault("data/assault.json");
        LoadTreeNodes("data/treenode.json");
        LoadWeapons("data/weapon.json", "data/spweapon.json");
        foreach (var f in Store.List("data").Where(f => Path.GetFileName(f).StartsWith("unlock_"))) LoadUnlock(f);
    }

    public string T(string? key, string? fallback = null)
    {
        if (string.IsNullOrEmpty(key)) return fallback ?? "";
        if (Text.TryGetValue(key, out var s) && s.Length > 0) return s;
        if (TextEn.TryGetValue(key, out s) && s.Length > 0) return s;
        return fallback ?? key;
    }

    // Returns the asset-relative path of the PNG for a UE asset path, or null when it is not bundled.
    public string? Image(string folder, string? assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        var name = assetPath;
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var rel = folder + "/" + name + ".png";
        return Store.Exists(rel) ? rel : null;
    }

    public byte[]? ImageBytes(string rel) => Store.ReadBytes(rel);

    Dictionary<string, string> LoadText(string rel)
    {
        var txt = Store.ReadText(rel);
        if (txt == null) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(txt) ?? new();
    }

    IEnumerable<JsonElement> Rows(string rel)
    {
        var txt = Store.ReadText(rel);
        if (txt == null) yield break;
        using var doc = JsonDocument.Parse(txt);
        if (!doc.RootElement.TryGetProperty("Rows", out var rows)) yield break;
        foreach (var r in rows.EnumerateObject()) yield return r.Value.Clone();
    }

    static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long I(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static string Asset(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? S(v, "AssetPathName") : "";
    static string Enum(JsonElement e, string name) { var s = S(e, name); int i = s.IndexOf("::"); return i >= 0 ? s[(i + 2)..] : s; }

    void LoadMedals(string p)
    {
        foreach (var r in Rows(p))
            Medals.Add(new MedalInfo
            {
                Id = (uint)I(r, "MedalId"),
                Name = T(S(r, "MedalNameTextId")),
                Description = T(S(r, "UnLockedDescriptionTextId")),
                Hint = T(S(r, "LockedDescriptionTextId")),
                Icon = Image("medal", Asset(r, "UnLockedIconImagePath")),
                LockedIcon = Image("medal", Asset(r, "LockedIconImagePath")),
            });
        Medals.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    void LoadAircraft(string p)
    {
        foreach (var r in Rows(p))
        {
            var sid = S(r, "PlaneStringID");
            var shortId = sid.Contains('_') ? sid[(sid.IndexOf('_') + 1)..] : sid;
            Aircraft.Add(new AircraftInfo
            {
                Id = (uint)I(r, "PlaneID"),
                StringId = sid,
                ShortId = shortId,
                Name = T(S(r, "PlaneNameTextID")),
                Nickname = T(S(r, "PlaneNicknameTextID")),
                Description = T(S(r, "PlaneDataViewerDescriptionTextID"), T(S(r, "PlaneDescriptionTextID"))),
                Category = Enum(r, "Category"),
                Cost = I(r, "PlaneBaseCost"),
                Sort = I(r, "SortNumber"),
                Icon = Image("aircraft", Asset(r, "PlaneIconFill")) ?? Image("aircraft", Asset(r, "PlaneIconBackGround")) ?? FindAircraftIcon(shortId),
                DefaultSkinId = (uint)I(r, "DefaultSkinID"),
                // Loaner aircraft for one mission, not sold in the tree: PP0024_f18f_ms01 (Mission 1), PP0013_a06e_ms15 (Mission 15).
                // A DLC can add more. The _msNN suffix gives the mission number.
                MissionOnly = B(r, "bNonCustomizable"),
                LoanMission = Regex.Match(sid, @"_ms(\d+)$") is { Success: true } lm ? int.Parse(lm.Groups[1].Value) : 0,
                Stats = new[] { I(r, "GraphAirToAir"), I(r, "GraphAirToGround"), I(r, "GraphSpeed"), I(r, "GraphMobility"), I(r, "GraphStability"), I(r, "GraphDefense") },
            });
        }
        Aircraft.Sort((a, b) => a.Sort.CompareTo(b.Sort));
    }

    void LoadWeapons(string weaponPath, string spPath)
    {
        foreach (var r in Rows(weaponPath))
        {
            var id = S(r, "WeaponID");
            if (id == "") continue;
            Weapons[id] = new WeaponInfo
            {
                Id = id,
                Name = T(S(r, "WeaponNameTextID"), Enum(r, "WeaponID")),
                ShortName = T(S(r, "WeaponShortNameTextID"), Enum(r, "WeaponID")),
                Description = T(S(r, "WeaponDescriptionTextID"), ""),
            };
        }
        var txt = Store.ReadText(spPath);
        if (txt == null) return;
        using var doc = JsonDocument.Parse(txt);
        foreach (var w in doc.RootElement.GetProperty("WeaponOrder").EnumerateArray()) WeaponOrder.Add(w.GetString() ?? "");
        foreach (var p in doc.RootElement.GetProperty("Planes").EnumerateObject())
        {
            var ac = Aircraft.FirstOrDefault(a => a.Id.ToString() == p.Name);
            if (ac == null) continue;
            // Slot 1 is the weapon the game gives with the aircraft. WID_NONE marks an unused slot.
            ac.SpWeapons = p.Value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v != "" && v != "ELiveWeaponID::WID_NONE").ToList();
        }
    }

    string? FindAircraftIcon(string shortId)
    {
        foreach (var cand in new[] { $"aircraft/T_HUD_acIcon_{shortId}.png", $"aircraft/T_HUD_acIcon_{shortId}_inside.png" })
            if (Store.Exists(cand)) return cand;
        return Store.List("aircraft").FirstOrDefault(f => f.Contains($"_{shortId}") && !f.Contains("_Lock") && !f.Contains("Unlock"));
    }

    void LoadSkins(string p)
    {
        foreach (var r in Rows(p))
        {
            var id = (uint)I(r, "SkinID");
            if (id == 0) continue;
            Skins.Add(new SkinInfo
            {
                Id = id,
                PlaneStringId = S(r, "PlaneStringID"),
                Name = T(S(r, "SkinNameTextID")),
                Description = T(S(r, "SkinDescriptionTextID")),
                Category = Enum(r, "SkinCategory"),
                Icon = Image("skin", Asset(r, "MenuIconImageRef")),
                Banner = Image("banner", Asset(r, "BannerAircraftImageRef")),
                Sort = I(r, "SortNumber"),
                Dlc = S(r, "DLCID"),
            });
        }
        Skins.Sort((a, b) => a.Sort.CompareTo(b.Sort));
    }

    void LoadEmblems(string p)
    {
        foreach (var r in Rows(p))
            Emblems.Add(new EmblemInfo
            {
                Id = (uint)I(r, "EmblemID"),
                Name = T(S(r, "EmblemNameTextID")),
                Description = T(S(r, "EmblemDescriptionTextID")),
                Category = Enum(r, "Category"),
                Icon = Image("emblem", Asset(r, "MenuIconImageRef")) ?? Image("emblem", Asset(r, "IconSFileRef")),
                Sort = I(r, "SortNumber"),
                Online = B(r, "IsOnline"),
            });
        Emblems.Sort((a, b) => a.Sort.CompareTo(b.Sort));
    }

    void LoadParts(string p)
    {
        foreach (var r in Rows(p))
        {
            var cat = Enum(r, "Category");          // PIC_01
            var catNum = cat.Length > 4 ? cat[4..] : cat;
            Parts.Add(new PartInfo
            {
                Id = (uint)I(r, "PartsID"),
                Name = T(S(r, "PartsNameTextID")),
                ShortName = T(S(r, "PartsShortNameTextID")),
                Description = T(S(r, "PartsDescriptionTextID")),
                Position = Enum(r, "PartsPosition"),
                Kind = Enum(r, "PartsIconKinds"),
                Cost = I(r, "PartsCost"),
                Sort = I(r, "SortNumber"),
                Icon = FindPartIcon(catNum),
            });
        }
        Parts.Sort((a, b) => a.Sort.CompareTo(b.Sort));
    }

    Dictionary<string, string>? partIconMap;
    // DA_LiveUIIconDataAsset maps EPartsIconCategory to the unlocked icon (first occurrence) and the locked one (second).
    string? FindPartIcon(string catNum)
    {
        if (partIconMap == null)
        {
            partIconMap = new();
            var txt = Store.ReadText("data/icons.json");
            if (txt != null)
                foreach (Match m in Regex.Matches(txt, "\"Key\": \"EPartsIconCategory::(PIC_\\d+)\",\\s*\"Value\": \\{\\s*\"AssetPathName\": \"([^\"]*)\""))
                    partIconMap.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }
        if (partIconMap.TryGetValue("PIC_" + catNum, out var asset)) { var img = Image("parts", asset); if (img != null) return img; }
        var n = int.TryParse(catNum, out var v) ? v.ToString() : catNum;
        var files = Store.List("parts").ToList();
        return files.FirstOrDefault(f => f.EndsWith($"_PartsCAT_{n}.png")) ?? files.FirstOrDefault(f => f.Contains($"_PartsCAT_{n}_S"));
    }

    void LoadMissions(string p)
    {
        foreach (var r in Rows(p))
        {
            var id = (int)I(r, "MissionID");
            if (Enum(r, "GameModeType") != "Campaign") continue;
            Missions.Add(new MissionInfo
            {
                Id = id,
                Number = T(S(r, "MissionNumberTextID")),
                Name = T(S(r, "MissionNameID")),
                Operation = T(S(r, "MissionOperationTextID")),
                Map = T(S(r, "MapNameID")),
                Objective = T(S(r, "MissionClearConditionID")),
                Sort = I(r, "StartupSortNumber"),
            });
        }
        Missions.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    void LoadAssault(string p)
    {
        foreach (var r in Rows(p))
            AssaultRecords.Add(new AssaultInfo
            {
                Id = (uint)I(r, "PilotDataId"),
                Name = T(S(r, "PilotNameTextId")),
                TacName = T(S(r, "TacNameTextId")),
                Rank = T(S(r, "RankTextId")),
                AircraftName = T(S(r, "AircraftNameTextId")),
                Unit = T(S(r, "UnitNameTextId")),
                Age = (int)I(r, "Age"),
                Hint = T(S(r, "UnlockedConditionTextId")),
                Profile = T(S(r, "FlavorTextId_2nd")),
                Icon = Image("named", Asset(r, "UnLockedIconImagePath")),
                Thumbnail = Image("named", Asset(r, "UnLockedDetailImagePath")),
            });
        AssaultRecords.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    void LoadTreeNodes(string p)
    {
        foreach (var r in Rows(p))
            TreeNodes.Add(new TreeNodeInfo
            {
                NodeId = (int)I(r, "NodeID"),
                Type = Enum(r, "NodeType"),
                ReferenceId = (uint)I(r, "ReferenceId"),
                Cost = I(r, "CostBase1"),
            });
    }

    void LoadUnlock(string rel)
    {
        var txt = Store.ReadText(rel);
        if (txt == null) return;
        using var doc = JsonDocument.Parse(txt);
        if (!doc.RootElement.TryGetProperty("Properties", out var props) || !props.TryGetProperty("ConfigurationEntryList", out var list)) return;
        foreach (var e in list.EnumerateArray())
        {
            var rule = new UnlockRule { Id = (uint)I(e, "ID") };
            if (e.TryGetProperty("ConditionEntryList", out var conds))
                foreach (var c in conds.EnumerateArray()) rule.Conditions.Add($"{Enum(c, "ConditionType")}: {S(c, "Parameters")}");
            if (e.TryGetProperty("ActionEntryList", out var acts))
                foreach (var a in acts.EnumerateArray()) rule.Actions.Add($"{Enum(a, "ActionType")}: {S(a, "Parameters")}");
            UnlockRules[rule.Id] = rule;
        }
    }
}

public sealed class MedalInfo { public uint Id; public string Name = ""; public string Description = ""; public string Hint = ""; public string? Icon; public string? LockedIcon; }
public sealed class AircraftInfo { public uint Id; public string StringId = ""; public string ShortId = ""; public string Name = ""; public string Nickname = ""; public string Description = ""; public string Category = ""; public long Cost; public long Sort; public string? Icon; public uint DefaultSkinId; public long[] Stats = Array.Empty<long>(); public List<string> SpWeapons = new(); public bool MissionOnly; public int LoanMission; }
public sealed class WeaponInfo { public string Id = ""; public string Name = ""; public string ShortName = ""; public string Description = ""; }
public sealed class SkinInfo { public uint Id; public string PlaneStringId = ""; public string Name = ""; public string Description = ""; public string Category = ""; public string? Icon; public string? Banner; public long Sort; public string Dlc = ""; }
public sealed class EmblemInfo { public uint Id; public string Name = ""; public string Description = ""; public string Category = ""; public string? Icon; public long Sort; public bool Online; }
public sealed class PartInfo { public uint Id; public string Name = ""; public string ShortName = ""; public string Description = ""; public string Position = ""; public string Kind = ""; public long Cost; public long Sort; public string? Icon; }
public sealed class MissionInfo { public int Id; public string Number = ""; public string Name = ""; public string Operation = ""; public string Map = ""; public string Objective = ""; public long Sort; }
public sealed class AssaultInfo { public uint Id; public string Name = ""; public string TacName = ""; public string Rank = ""; public string AircraftName = ""; public string Unit = ""; public int Age; public string Hint = ""; public string Profile = ""; public string? Icon; public string? Thumbnail; }
public sealed class TreeNodeInfo { public int NodeId; public string Type = ""; public uint ReferenceId; public long Cost; }
public sealed class UnlockRule { public uint Id; public List<string> Conditions = new(); public List<string> Actions = new(); }
