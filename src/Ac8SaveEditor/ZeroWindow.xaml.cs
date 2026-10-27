using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ac8Save;
using AczSave;

namespace Ac8SaveEditor;

public partial class ZeroWindow : Window
{
    const uint AppId = 2288340;
    static readonly string[] LevelNames = { "Very Easy", "Easy", "Normal", "Hard", "Expert", "Ace" };
    static readonly string[] StyleNames = { "Mercenary", "Soldier", "Knight" };
    static readonly string[] RankNames = { "S", "A", "B", "C", "-" };
    static readonly string[] ColorNames = { "Standard color", "Mercenary color", "Soldier color", "Knight color", "SP color" };

    sealed record AcInfo(int index, string code, string name, int order, int price, int[] weaponPrices, int[] stats,
        string? role = null, string? description = null, string? unlock = null, string[]? weapons = null, string? image = null, Dictionary<string, string>? colors = null);
    sealed record MedalInfo(int index, string description, string condition);
    sealed record RivalInfo(int index, string bio, string fate);
    sealed record MissionInfo(int index, string number, string name, string title);

    readonly AssetStore assets;
    readonly List<AcInfo> aircraft = new();
    readonly List<MedalInfo> medals = new();
    readonly List<RivalInfo> rivals = new();
    readonly List<MissionInfo> missions = new();

    string? savePath;
    uint accountId;
    RawSave? raw;
    Campaign? cm;
    bool dirty;
    string page = "overview", search = "";
    Action? refresh; Action<bool>? setAll;

    static string L(string en) => MainWindow.L(en);

    public ZeroWindow(string? path = null)
    {
        InitializeComponent();
        var zip = Path.Combine(AppContext.BaseDirectory, "assets-acz.zip");
        assets = new AssetStore(File.Exists(zip) ? zip : Path.Combine(AppContext.BaseDirectory, "assets-acz"));
        LoadAssets();
        ApplyUiLanguage();
        savePath = path ?? FindSave();
        LoadSave();
        Nav.SelectedIndex = 0;
    }

    void ApplyUiLanguage()
    {
        BtnBrowse.Content = L("Save file...");
        BtnReload.Content = L("Reload");
        BtnSave.Content = L("Save (with backup)");
        LblSearch.Text = L("Search:");
        BtnAll.Content = L("Check all");
        BtnNone.Content = L("Uncheck all");
        LblSlot.Text = L("Slot:");
        LblDetailHint.Text = L("Select an item to see details.");
        var navNames = new[] { "Overview", "Hangar", "Missions", "Campaign log", "Flight data", "Medals", "Assault records" };
        for (int i = 0; i < Nav.Items.Count; i++) ((ListBoxItem)Nav.Items[i]).Content = L(navNames[i]);
    }

    void LoadAssets()
    {
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        T[] Read<T>(string name) { var t = assets.ReadText(name); return t == null ? Array.Empty<T>() : JsonSerializer.Deserialize<T[]>(t, opt) ?? Array.Empty<T>(); }
        aircraft.AddRange(Read<AcInfo>("aircraft.json"));
        medals.AddRange(Read<MedalInfo>("medals.json"));
        rivals.AddRange(Read<RivalInfo>("rivals.json"));
        missions.AddRange(Read<MissionInfo>("missions.json"));
        if (aircraft.Count == 0)
            for (int i = 0; i < Campaign.Aircraft.Count; i++) aircraft.Add(new AcInfo(i, "", $"Aircraft {i}", Array.IndexOf(Campaign.Aircraft.DisplayOrder, i), 0, new int[2], new int[6]));
        for (int i = missions.Count; i < Campaign.MissionCount; i++) missions.Add(new MissionInfo(i, i == 18 ? "SP" : $"{i + 1:00}", "", ""));
        for (int i = medals.Count; i < Campaign.Rival.MedalCount; i++) medals.Add(new MedalInfo(i, "", ""));
        for (int i = rivals.Count; i < Campaign.Rival.Count; i++) rivals.Add(new RivalInfo(i, "", ""));
    }


    /// Looks for <Steam>\userdata\<account>\2288340\remote\aczero\save\save.sav; the account id is the folder name.
    public static string? FindSave()
    {
        var roots = new List<string>();
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\'));
        }
        catch { }
        roots.Add(@"C:\Program Files (x86)\Steam");
        foreach (var root in roots.Distinct())
        {
            var ud = Path.Combine(root, "userdata");
            if (!Directory.Exists(ud)) continue;
            foreach (var acc in Directory.GetDirectories(ud))
            {
                var p = Path.Combine(acc, AppId.ToString(), "remote", "aczero", "save", "save.sav");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    static bool TryAccountFromPath(string path, out uint acc)
    {
        acc = 0;
        var parts = path.Replace('/', '\\').Split('\\');
        int i = Array.FindIndex(parts, p => string.Equals(p, "userdata", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length && uint.TryParse(parts[i + 1], out acc);
    }

    void LoadSave()
    {
        raw = null; cm = null; dirty = false;
        CmbSlot.Items.Clear();
        BtnSave.IsEnabled = false;
        if (savePath == null || !File.Exists(savePath))
        {
            LblFile.Text = "";
            LblStatus.Text = L("save.sav not found. Use \"Save file...\" and pick <Steam>\\userdata\\<account>\\2288340\\remote\\aczero\\save\\save.sav.");
            return;
        }
        LblFile.Text = savePath;
        if (!TryAccountFromPath(savePath, out accountId))
        {
            var dlg = new AccountDialog { Owner = this };
            if (dlg.ShowDialog() != true) { LblStatus.Text = L("Steam account id is needed to decode this file."); return; }
            accountId = dlg.AccountId;
        }
        try
        {
            var bytes = File.ReadAllBytes(savePath);
            bool rekeyed = false;
            byte[] decoded;
            bool outerOk;
            try { decoded = Container.Decode(bytes, accountId, out outerOk); }
            catch (InvalidDataException) { decoded = []; outerOk = false; }
            if (!outerOk && Container.TryFindKey(bytes, out uint foundKey))
            {
                decoded = Container.DecodeWithKey(bytes, foundKey, out outerOk);
                rekeyed = true;
            }
            if (decoded.Length == 0) throw new InvalidDataException(L("Steam account id does not match this file."));
            var r = new RawSave(decoded);
            var problems = new List<string>();
            if (!outerOk) problems.Add(L("outer checksum mismatch"));
            if (!r.IsValid) problems.Add(L("header magic mismatch"));
            if (!r.ChecksumOk) problems.Add(L("inner checksum mismatch"));
            var back = Container.Decode(Container.Encode(r.Data, accountId), accountId, out _);
            if (!back.AsSpan().SequenceEqual(r.Data)) problems.Add(L("round-trip mismatch"));
            raw = r;
            for (int s = 0; s < RawSave.SlotCount; s++)
            {
                var e = r.GetSlotEntry(s);
                CmbSlot.Items.Add(e.IsEmpty ? $"{s + 1}: {L("empty")}" : $"{s + 1}: M{e.Mission + 1:00} {e.Hours}:{e.Minutes:00}:{e.Seconds:00}");
            }
            CmbSlot.SelectedIndex = Math.Clamp(r.LastSlot, 0, RawSave.SlotCount - 1);
            BtnSave.IsEnabled = problems.Count == 0;
            LblStatus.Text = problems.Count == 0 ? L("save.sav loaded. Checksums valid.") : string.Join(" | ", problems) + " - " + L("saving disabled");
            if (rekeyed && problems.Count == 0)
                LblStatus.Text += " " + string.Format(L("This file was encrypted for another Steam account. Saving re-encrypts it for account {0}."), accountId);
        }
        catch (Exception ex) { LblStatus.Text = $"{L("Cannot read save.sav")}: {ex.Message}"; }
    }

    void Slot_Changed(object s, SelectionChangedEventArgs e)
    {
        if (raw == null || CmbSlot.SelectedIndex < 0) return;
        cm = raw.GetCampaign(CmbSlot.SelectedIndex);
        ShowPage(page);
    }

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L("Select save.sav"), Filter = "save.sav|save.sav|*.sav|*.sav", InitialDirectory = savePath != null ? Path.GetDirectoryName(savePath) : null };
        if (dlg.ShowDialog() != true) return;
        if (dirty && MessageBox.Show(L("Discard unsaved changes?"), "AC Zero", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        savePath = dlg.FileName;
        LoadSave();
        ShowPage(page);
    }

    void Reload_Click(object s, RoutedEventArgs e)
    {
        if (dirty && MessageBox.Show(L("Discard unsaved changes?"), "AC Zero", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        LoadSave();
        ShowPage(page);
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        if (raw == null || savePath == null) return;
        try
        {
            var dir = Path.GetDirectoryName(savePath)!;
            var bak = Path.Combine(dir, "backup_ac8edit");
            Directory.CreateDirectory(bak);
            File.Copy(savePath, Path.Combine(bak, $"save_{DateTime.Now:yyyyMMdd_HHmmss}.sav"), true);
            SyncHeader();
            raw.UpdateChecksum();
            File.WriteAllBytes(savePath, Container.Encode(raw.Data, accountId));
            dirty = false;
            LblStatus.Text = $"{L("Saved")}: {savePath}. {L("Backup in")} {bak}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, L("Save failed"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// The load screen reads mission, difficulty and play time from the header, not from the slot.
    void SyncHeader()
    {
        if (raw == null) return;
        for (int s = 0; s < RawSave.SlotCount; s++)
        {
            var c = raw.GetCampaign(s);
            if (c.IsEmpty) continue;
            var e = raw.GetSlotEntry(s);
            e.Mission = c.CurrentMission;
            e.Level = c.FreeMissionLevel;
            e.SetPlayTime((int)c.Records.PlayTimeSeconds);
        }
    }

    void MarkDirty(string what)
    {
        dirty = true;
        LblStatus.Text = $"{L("Modified")}: {what}. {L("Not saved yet.")}";
    }


    void Nav_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem it && it.Tag is string tag) ShowPage(tag);
    }
    void Search_TextChanged(object s, TextChangedEventArgs e) { search = TxtSearch.Text.Trim(); refresh?.Invoke(); }
    void CheckAll_Click(object s, RoutedEventArgs e) => setAll?.Invoke(true);
    void UncheckAll_Click(object s, RoutedEventArgs e) => setAll?.Invoke(false);

    void ShowPage(string tag)
    {
        page = tag;
        refresh = null; setAll = null;
        BtnAll.Visibility = BtnNone.Visibility = Visibility.Collapsed;
        LblPageInfo.Text = "";
        Detail.Children.Clear();
        Detail.Children.Add(Muted(L("Select an item to see details.")));
        if (Nav.SelectedItem is ListBoxItem nav) LblPageTitle.Text = nav.Content as string ?? "";
        if (cm == null || cm.IsEmpty)
        {
            PageHost.Content = Muted(raw == null ? L("No save loaded.") : L("This slot is empty. Start a game in this slot first."));
            return;
        }
        switch (tag)
        {
            case "overview": PageOverview(); break;
            case "hangar": PageHangar(); break;
            case "missions": PageMissions(); break;
            case "log": PageCampaignLog(); break;
            case "flight": PageFlightData(); break;
            case "medals": PageMedals(); break;
            case "aces": PageAces(); break;
        }
    }


    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static TextBlock Muted(string t, double size = 13) => new() { Text = t, Foreground = Res("Fg2"), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
    static TextBlock Label(string t, double size = 13, bool bold = false) => new() { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    readonly Dictionary<string, BitmapImage> imgCache = new();
    BitmapImage? Img(string? rel, int decodeWidth)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        var key = rel + "#" + decodeWidth;
        if (imgCache.TryGetValue(key, out var b)) return b;
        var bytes = assets.ReadBytes(rel);
        if (bytes == null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(bytes);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.EndInit();
            bi.Freeze();
            imgCache[key] = bi;
            return bi;
        }
        catch { return null; }
    }

    Image ImageBox(string? rel, double w, double h, int decode)
    {
        var img = new Image { Width = w, Height = h, Stretch = Stretch.Uniform, Source = Img(rel, decode) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    sealed class CardItem
    {
        public string Title = ""; public string Sub = ""; public string? Image; public double ImgW = 96, ImgH = 96; public int Decode = 0; public double Width = 200;
        public Func<bool> Get = () => false; public Action<bool> Set = _ => { };
        public Action? ShowDetail; public string SearchText = "";
        public List<CardOption> Options = new();
    }
    sealed record CardOption(string Label, string Tip, Func<bool> Get, Action<bool> Set, bool AlwaysOn = false);

    bool Visible(CardItem it) => search.Length == 0 || it.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase) || it.Title.Contains(search, StringComparison.OrdinalIgnoreCase);

    ScrollViewer CardGrid(List<CardItem> items, string what, string onText, string offText)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        void Build()
        {
            panel.Children.Clear();
            int shown = 0, on = 0;
            foreach (var it in items)
            {
                if (!Visible(it)) continue;
                shown++;
                bool state = it.Get();
                if (state) on++;
                var border = new Border { Style = (Style)Application.Current.Resources["Card"], Width = it.Width, Cursor = System.Windows.Input.Cursors.Hand };
                var stack = new StackPanel();
                if (it.Image != null) { var img = ImageBox(it.Image, it.ImgW, it.ImgH, it.Decode); img.Opacity = state ? 1 : 0.35; stack.Children.Add(img); }
                var title = Label(it.Title, 12, true); title.TextTrimming = TextTrimming.CharacterEllipsis; title.MaxHeight = 36; title.ToolTip = it.Title;
                stack.Children.Add(title);
                if (it.Sub.Length > 0) { var sub = Muted(it.Sub, 11); sub.TextTrimming = TextTrimming.CharacterEllipsis; sub.MaxHeight = 48; sub.ToolTip = it.Sub; stack.Children.Add(sub); }
                var chk = new CheckBox { Content = state ? onText : offText, IsChecked = state, Margin = new Thickness(0, 4, 0, 0), Foreground = state ? Res("Ok") : Res("Fg2") };
                chk.Checked += (_, _) => { it.Set(true); MarkDirty(it.Title); Build(); };
                chk.Unchecked += (_, _) => { it.Set(false); MarkDirty(it.Title); Build(); };
                stack.Children.Add(chk);
                foreach (var opt in it.Options)
                {
                    bool optOn = opt.Get();
                    var oc = new CheckBox { Content = opt.Label, ToolTip = opt.Tip, IsChecked = optOn, IsEnabled = state || opt.AlwaysOn, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Foreground = optOn ? Res("Fg") : Res("Fg2") };
                    oc.Checked += (_, _) => { opt.Set(true); MarkDirty($"{it.Title}: {opt.Label}"); Build(); };
                    oc.Unchecked += (_, _) => { opt.Set(false); MarkDirty($"{it.Title}: {opt.Label}"); Build(); };
                    stack.Children.Add(oc);
                }
                border.Child = stack;
                border.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) it.ShowDetail?.Invoke(); };
                panel.Children.Add(border);
            }
            LblPageInfo.Text = $"{on} {L("of")} {shown} {what}" + (shown != items.Count ? $" ({L("filtered from")} {items.Count})" : "");
        }
        refresh = Build;
        setAll = v =>
        {
            foreach (var it in items)
            {
                if (!Visible(it)) continue;
                it.Set(v);
                if (v) foreach (var opt in it.Options) opt.Set(true);
            }
            MarkDirty(v ? L("check all") : L("uncheck all"));
            Build();
        };
        BtnAll.Visibility = BtnNone.Visibility = Visibility.Visible;
        Build();
        return sv;
    }

    void ShowDetailPanel(string? bigImage, double imgH, string title, params (string label, string value)[] rows)
    {
        Detail.Children.Clear();
        if (bigImage != null) { var img = ImageBox(bigImage, 320, imgH, 0); img.HorizontalAlignment = HorizontalAlignment.Left; Detail.Children.Add(img); }
        Detail.Children.Add(Label(title, 16, true));
        foreach (var (l, v) in rows)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            Detail.Children.Add(Muted(l, 11));
            Detail.Children.Add(Label(v));
        }
    }


    void PageOverview()
    {
        var c = cm!;
        var panel = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var p = c.Planes;
        int owned = 0; for (int i = 0; i < Campaign.Aircraft.Count; i++) if (p.Owned(i)) owned++;
        int medalCount = 0; for (int i = 0; i < Campaign.Rival.MedalCount; i++) if (c.Rivals.Medal(i)) medalCount++;
        int aces = 0; for (int i = 0; i < Campaign.Rival.Count; i++) if (c.Rivals.Destroyed(i)) aces++;
        var t = c.Records.PlayTimeSeconds;

        void Stat(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var l = Muted(label); l.Width = 220; row.Children.Add(l);
            row.Children.Add(Label(value));
            panel.Children.Add(row);
        }
        void NumField(string label, long value, Action<long> set)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var l = Muted(label); l.Width = 220; row.Children.Add(l);
            var tb = new TextBox { Text = value.ToString(), Width = 120 };
            var btn = new Button { Content = L("Apply"), Margin = new Thickness(8, 0, 0, 0) };
            btn.Click += (_, _) => { if (long.TryParse(tb.Text, out var v)) { set(v); MarkDirty(label); ShowPage("overview"); } };
            row.Children.Add(tb); row.Children.Add(btn);
            panel.Children.Add(row);
        }
        Stat(L("Slot"), (c.Index + 1).ToString());
        Stat(L("Play time"), $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}");
        Stat(L("Aircraft owned"), $"{owned} / {Campaign.Aircraft.Count}");
        Stat(L("Medals"), $"{medalCount} / {Campaign.Rival.MedalCount}");
        Stat(L("Assault records"), $"{aces} / {Campaign.Rival.Count}");
        panel.Children.Add(Label(L("Edit"), 15, true));
        NumField(L("Credits"), p.Money, v => p.Money = (uint)Math.Clamp(v, 0, uint.MaxValue));
        NumField(L("Current mission (1-18, 19 = after ending)"), c.CurrentMission + 1, v => c.CurrentMission = (int)Math.Clamp(v - 1, 0, 18));
        NumField(L("Ace style gauge (< -40 Mercenary, -40..40 Soldier, > 40 Knight)"), c.AceStyleGauge, v => c.AceStyleGauge = (int)Math.Clamp(v, -999, 999));

        var timeRow = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var tl = Muted(L("Play time (h:mm:ss)")); tl.Width = 220; timeRow.Children.Add(tl);
        var ttb = new TextBox { Text = FormatTime(t), Width = 120 };
        var tbtn = new Button { Content = L("Apply"), Margin = new Thickness(8, 0, 0, 0) };
        tbtn.Click += (_, _) => { if (TryParseTime(ttb.Text, out var secs)) { var rec = c.Records; rec.PlayTimeSeconds = secs; MarkDirty(L("Play time")); ShowPage("overview"); } };
        timeRow.Children.Add(ttb); timeRow.Children.Add(tbtn);
        panel.Children.Add(timeRow);

        var selRow = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var sl = Muted(L("Aircraft in the hangar slot")); sl.Width = 220; selRow.Children.Add(sl);
        var ownedList = aircraft.OrderBy(a => a.order).Where(a => p.Owned(a.index) || a.index == p.SelectedAircraft(0)).ToList();
        var selCb = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = ownedList.Select(a => a.name).ToList(), SelectedIndex = ownedList.FindIndex(a => a.index == p.SelectedAircraft(0)) };
        selCb.SelectionChanged += (_, _) =>
        {
            if (selCb.SelectedIndex < 0) return;
            p.SetSelectedAircraft(0, ownedList[selCb.SelectedIndex].index); p.SetSelectedColor(0, 0); p.SetSelectedWeapon(0, 0);
            MarkDirty(L("Aircraft in the hangar slot"));
        };
        selRow.Children.Add(selCb);
        panel.Children.Add(selRow);

        var lvlRow = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var ll = Muted(L("Difficulty")); ll.Width = 220; lvlRow.Children.Add(ll);
        var cb = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = LevelNames.Select(L).ToList(), SelectedIndex = Math.Clamp(c.FreeMissionLevel, 0, 5) };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { c.FreeMissionLevel = cb.SelectedIndex; MarkDirty(L("difficulty")); } };
        lvlRow.Children.Add(cb);
        panel.Children.Add(lvlRow);

        panel.Children.Add(Label(L("Shortcuts"), 15, true));
        var btnAll = new Button { Content = L("Unlock and buy every aircraft, color and special weapon"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        btnAll.Click += (_, _) =>
        {
            for (int i = 0; i < Campaign.Aircraft.Count; i++)
            {
                for (int k = 0; k < Campaign.Aircraft.ColorSlots; k++) { p.SetUnlocked(i, k, true); p.SetOwned(i, k, true); }
                for (int w = 0; w < Campaign.Aircraft.WeaponSlots; w++) { p.SetWeaponAvailable(i, w, true); p.SetWeaponOwned(i, w, true); }
            }
            MarkDirty(L("all aircraft")); ShowPage("overview");
        };
        panel.Children.Add(btnAll);
        var btnS = new Button { Content = L("Mark every mission S on every difficulty and ace style"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        btnS.Click += (_, _) =>
        {
            for (int l = 0; l < Campaign.LevelCount; l++)
                for (int m = 0; m < Campaign.MissionCount; m++)
                    for (int st = 0; st < Campaign.StyleCount; st++)
                    {
                        c.Records.SetClear(l, m, st, true); c.Records.SetClear(6, m, st, true); c.Records.SetRank(l, m, st, 0);
                    }
            MarkDirty(L("all missions S")); ShowPage("overview");
        };
        panel.Children.Add(btnS);
        var btnSeen = new Button { Content = L("Clear every [NEW] marker in Assault Records"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        btnSeen.Click += (_, _) =>
        {
            for (int i = 0; i < Campaign.Rival.Count; i++) c.Rivals.SetNew(i, false);
            MarkDirty(L("[NEW] markers")); ShowPage("overview");
        };
        panel.Children.Add(btnSeen);
        panel.Children.Add(Muted(L("Mission scores and times are kept as they are. Set them per mission on the Missions page.")));
        PageHost.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    void PageHangar()
    {
        var c = cm!; var p = c.Planes;
        var items = new List<CardItem>();
        foreach (var ac in aircraft.OrderBy(a => a.order))
        {
            int i = ac.index;
            var it = new CardItem
            {
                Title = ac.name, Sub = (ac.role != null ? L(ac.role) + "  |  " : "") + (ac.price > 0 ? $"{ac.price:N0} cr" : L("starting aircraft")),
                Image = ac.image, ImgW = 274, ImgH = 128, Decode = 548, Width = 296,
                SearchText = ac.code + " " + ac.name + " " + ac.role + " " + string.Join(" ", ac.weapons ?? Array.Empty<string>()),
                Get = () => p.Owned(i, 0),
                Set = v => { p.SetOwned(i, 0, v); if (v) { p.SetUnlocked(i, 0, true); p.SetWeaponAvailable(i, 0, true); p.SetWeaponOwned(i, 0, true); } else for (int k = 1; k < Campaign.Aircraft.ColorSlots; k++) p.SetOwned(i, k, false); },
            };
            it.Options.Add(new CardOption(L("Unlocked in shop"), L("Listed in the shop; the game sets this when a mission unlocks the aircraft"),
                () => p.Unlocked(i, 0), v => { p.SetUnlocked(i, 0, v); if (v) for (int w = 0; w < Campaign.Aircraft.WeaponSlots; w++) p.SetWeaponAvailable(i, w, true); }, true));
            for (int k = 1; k < Campaign.Aircraft.ColorSlots; k++)
            {
                int kk = k;
                it.Options.Add(new CardOption(L(ColorNames[k]), L("Owned color scheme"), () => p.Owned(i, kk) && p.Unlocked(i, kk), v => { p.SetOwned(i, kk, v); if (v) p.SetUnlocked(i, kk, true); }));
            }
            for (int w = 0; w < Campaign.Aircraft.WeaponSlots; w++)
            {
                int ww = w;
                var wname = ac.weapons != null && ac.weapons.Length > w && ac.weapons[w].Length > 0 ? $"SP{w + 1} {ac.weapons[w]}" : $"SP{w + 1}";
                it.Options.Add(new CardOption(wname, L("Special weapon owned"), () => p.WeaponOwned(i, ww), v => { if (v) p.SetWeaponAvailable(i, ww, true); p.SetWeaponOwned(i, ww, v); }));
            }
            it.ShowDetail = () => ShowAircraftDetail(ac, p);
            items.Add(it);
        }
        PageHost.Content = CardGrid(items, L("aircraft"), L("Owned"), L("Not owned"));
    }

    static readonly string[] StatNames = { "Speed", "Mobility", "Stability", "Defense", "Air-to-air", "Air-to-ground" };
    static readonly string[] ColorKinds = { "standard", "mercenary", "soldier", "knight", "special" };

    void ShowAircraftDetail(AcInfo ac, Campaign.Aircraft p)
    {
        int i = ac.index;
        Detail.Children.Clear();
        var big = ImageBox(ac.image, 310, 145, 620); big.HorizontalAlignment = HorizontalAlignment.Left;
        Detail.Children.Add(big);
        Detail.Children.Add(Label(ac.name, 16, true));
        if (ac.role != null) Detail.Children.Add(Muted(L(ac.role)));
        if (!string.IsNullOrEmpty(ac.description)) Detail.Children.Add(Label(ac.description, 12));

        var stats = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        for (int k = 0; k < Math.Min(ac.stats.Length, StatNames.Length); k++)
        {
            stats.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            var n = Muted(L(StatNames[k]), 11); Grid.SetRow(n, k); stats.Children.Add(n);
            var track = new Border { Background = Res("Panel2"), Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Background = Res("Accent"), Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 1.4 * Math.Clamp(ac.stats[k], 0, 100) };
            var bar = new Grid(); bar.Children.Add(track); bar.Children.Add(fill);
            Grid.SetRow(bar, k); Grid.SetColumn(bar, 1); stats.Children.Add(bar);
            var v = Label(ac.stats[k].ToString(), 11); v.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(v, k); Grid.SetColumn(v, 2); stats.Children.Add(v);
        }
        Detail.Children.Add(stats);

        void Row(string label, string? value) { if (string.IsNullOrWhiteSpace(value)) return; Detail.Children.Add(Muted(label, 11)); Detail.Children.Add(Label(value)); }
        Row(L("Price"), ac.price > 0 ? $"{ac.price:N0} cr" : L("starting aircraft"));
        if (ac.weapons != null)
            Row(L("Special weapons"), string.Join(Environment.NewLine, ac.weapons.Select((w, k) => $"SP{k + 1}: {w}" + (k > 0 && k - 1 < ac.weaponPrices.Length ? $"  ({ac.weaponPrices[k - 1]:N0} cr)" : "") + (p.WeaponOwned(i, k) ? "" : $"  - {L("not owned")}"))));
        Row(L("How to unlock"), ac.unlock);
        int allSorties = Enumerable.Range(0, Campaign.Aircraft.Count).Sum(a => p.Stat(Campaign.Aircraft.StatSorties, a));
        int sorties = p.Stat(Campaign.Aircraft.StatSorties, i);
        Row(L("Sorties"), sorties.ToString());
        Row(L("Usage"), allSorties > 0 ? $"{100.0 * sorties / allSorties:0.00}%" : "0.00%");
        Row(L("Targets destroyed (air + ground)"), $"{p.Stat(Campaign.Aircraft.StatKills, i)} ({p.Stat(Campaign.Aircraft.StatAirKills, i)} + {p.Stat(Campaign.Aircraft.StatGroundKills, i)})");

        if (ac.colors != null && ac.colors.Count > 1)
        {
            Detail.Children.Add(Muted(L("Color schemes"), 11));
            var wrap = new WrapPanel();
            foreach (var kind in ColorKinds)
            {
                if (!ac.colors.TryGetValue(kind, out var path)) continue;
                var st = new StackPanel { Margin = new Thickness(0, 0, 6, 6), Width = 150 };
                var img = ImageBox(path, 150, 70, 300); img.ToolTip = L(char.ToUpper(kind[0]) + kind[1..]);
                st.Children.Add(img);
                st.Children.Add(Muted(L(char.ToUpper(kind[0]) + kind[1..]), 11));
                wrap.Children.Add(st);
            }
            Detail.Children.Add(wrap);
        }
        Row(L("Internal index / code"), $"{i} / {ac.code}");
    }

    void PageMissions()
    {
        var c = cm!; var rec = c.Records;
        var list = new StackPanel();
        foreach (var mi in missions)
        {
            int m = mi.index;
            var card = new Border { Style = (Style)Application.Current.Resources["Card"] };
            var stack = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (mi.title.Length > 0) { var img = ImageBox(mi.title, 80, 70, 160); img.Margin = new Thickness(0, 0, 10, 0); head.Children.Add(img); }
            var title = Label($"{L("Mission")} {mi.number}  {mi.name}", 14, true); title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            var txt = new StackPanel();
            txt.Children.Add(head);
            var styles = new System.Windows.Controls.Primitives.UniformGrid { Columns = Campaign.StyleCount };
            Grid.SetIsSharedSizeScope(styles, true);
            styles.SizeChanged += (_, e) => styles.Columns = Math.Clamp((int)(e.NewSize.Width / 240), 1, Campaign.StyleCount);
            for (int st = 0; st < Campaign.StyleCount; st++)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 10, 8) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "lvl" });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var h = Muted(L(StyleNames[st]), 11); Grid.SetColumn(h, 1); grid.Children.Add(h);
                grid.RowDefinitions.Add(new RowDefinition());
                for (int l = 0; l < Campaign.LevelCount; l++)
                {
                    int lvl = l, sty = st;
                    grid.RowDefinitions.Add(new RowDefinition());
                    var ln = Muted(L(LevelNames[l]), 11); ln.Margin = new Thickness(0, 2, 8, 2); ln.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(ln, l + 1); grid.Children.Add(ln);
                    var cell = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 40 });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 52 });
                    var clr = new CheckBox { IsChecked = rec.Clear(lvl, m, sty), ToolTip = L("Cleared"), VerticalAlignment = VerticalAlignment.Center };
                    var rank = new ComboBox { ItemsSource = RankNames, SelectedIndex = Math.Clamp(rec.Rank(lvl, m, sty), 0, 4), Margin = new Thickness(4, 0, 4, 0), ToolTip = L("Rank") };
                    uint time = rec.Time(lvl, m, sty), score = rec.Score(lvl, m, sty);
                    var tb = new TextBox { Text = time == uint.MaxValue ? "" : time.ToString(), ToolTip = L("Time (s)") };
                    var sb = new TextBox { Text = score == uint.MaxValue ? "" : score.ToString(), ToolTip = L("Score"), Margin = new Thickness(4, 0, 0, 0) };
                    clr.Checked += (_, _) => { rec.SetClear(lvl, m, sty, true); rec.SetClear(6, m, sty, true); MarkDirty($"M{mi.number} {LevelNames[lvl]} {StyleNames[sty]}"); };
                    clr.Unchecked += (_, _) => { rec.SetClear(lvl, m, sty, false); MarkDirty($"M{mi.number} {LevelNames[lvl]} {StyleNames[sty]}"); };
                    rank.SelectionChanged += (_, _) => { if (rank.SelectedIndex >= 0) { rec.SetRank(lvl, m, sty, rank.SelectedIndex); if (rank.SelectedIndex < 4) { rec.SetClear(lvl, m, sty, true); rec.SetClear(6, m, sty, true); clr.IsChecked = true; } MarkDirty($"M{mi.number} rank"); } };
                    tb.LostFocus += (_, _) => { if (tb.Text.Length == 0) rec.SetTime(lvl, m, sty, 0, uint.MaxValue); else if (uint.TryParse(tb.Text, out var v)) rec.SetTime(lvl, m, sty, 0, v); MarkDirty($"M{mi.number} time"); };
                    sb.LostFocus += (_, _) => { if (sb.Text.Length == 0) rec.SetScore(lvl, m, sty, 0, uint.MaxValue); else if (uint.TryParse(sb.Text, out var v)) rec.SetScore(lvl, m, sty, 0, v); MarkDirty($"M{mi.number} score"); };
                    Grid.SetColumn(rank, 1); Grid.SetColumn(tb, 2); Grid.SetColumn(sb, 3);
                    cell.Children.Add(clr); cell.Children.Add(rank); cell.Children.Add(tb); cell.Children.Add(sb);
                    Grid.SetRow(cell, l + 1); Grid.SetColumn(cell, 1); grid.Children.Add(cell);
                }
                styles.Children.Add(grid);
            }
            txt.Children.Add(styles);
            stack.Children.Add(txt);
            card.Child = stack;
            card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is TextBlock or Border or Image) ShowMissionDetail(mi); };
            list.Children.Add(card);
        }
        list.Children.Insert(0, Muted(L("Per difficulty and ace style: cleared, rank, best time (s), best score. Empty = no record.")));
        PageHost.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
    }

    static string FormatTime(uint t) => $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}";

    static bool TryParseTime(string text, out uint seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        uint total = 0;
        foreach (var part in parts)
        {
            if (!uint.TryParse(part, out var v)) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    TextBox IntBox(int value, string tip, Action<int> set, double width = 0)
    {
        var tb = new TextBox { Text = value.ToString(), ToolTip = tip, Margin = new Thickness(2, 1, 2, 1) };
        if (width > 0) tb.Width = width;
        tb.LostFocus += (_, _) =>
        {
            if (!int.TryParse(tb.Text, out var v)) { tb.Text = value.ToString(); return; }
            if (v == value) return;
            value = v; set(v); MarkDirty(tip);
        };
        return tb;
    }

    static ScrollViewer Wide(UIElement table)
    {
        var sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = table };
        // A nested ScrollViewer eats the wheel even when it cannot scroll vertically; hand it to the page.
        sv.PreviewMouseWheel += (o, e) =>
        {
            if (sv.Parent is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = o });
        };
        return sv;
    }

    static TextBlock Cell(string text, bool header = false)
    {
        var t = header ? Muted(text, 11) : Label(text, 12);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(4, 1, 8, 1);
        return t;
    }

    void ShowMissionDetail(MissionInfo mi)
    {
        var rec = cm!.Records;
        int m = mi.index;
        Detail.Children.Clear();
        if (mi.title.Length > 0) { var img = ImageBox(mi.title, 320, 280, 0); img.HorizontalAlignment = HorizontalAlignment.Left; Detail.Children.Add(img); }
        Detail.Children.Add(Label($"{L("Mission")} {mi.number} {mi.name}", 16, true));
        Detail.Children.Add(Muted(L("Best three results (time ascending). Empty = no record.")));
        var lvl = new ComboBox { ItemsSource = LevelNames.Select(L).ToList(), SelectedIndex = Math.Clamp(cm.FreeMissionLevel, 0, 5), Margin = new Thickness(0, 4, 0, 2) };
        var sty = new ComboBox { ItemsSource = StyleNames.Select(L).ToList(), SelectedIndex = Math.Clamp(MissionResult.ClassOf(cm.AceStyleGauge), 0, 2), Margin = new Thickness(0, 2, 0, 4) };
        var host = new StackPanel();
        void Build()
        {
            host.Children.Clear();
            int l = lvl.SelectedIndex, st = sty.SelectedIndex;
            if (l < 0 || st < 0) return;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition());
            var h1 = Cell(L("Time (s)"), true); Grid.SetColumn(h1, 1); g.Children.Add(h1);
            var h2 = Cell(L("Score"), true); Grid.SetColumn(h2, 2); g.Children.Add(h2);
            for (int i = 0; i < 3; i++)
            {
                int ii = i;
                g.RowDefinitions.Add(new RowDefinition());
                var n = Cell($"#{i + 1}", true); Grid.SetRow(n, i + 1); g.Children.Add(n);
                uint time = rec.Time(l, m, st, i), score = rec.Score(l, m, st, i);
                var tb = new TextBox { Text = time == uint.MaxValue ? "" : time.ToString(), ToolTip = L("Time (s)"), Margin = new Thickness(2) };
                var sb = new TextBox { Text = score == uint.MaxValue ? "" : score.ToString(), ToolTip = L("Score"), Margin = new Thickness(2) };
                tb.LostFocus += (_, _) => { if (tb.Text.Length == 0) rec.SetTime(l, m, st, ii, uint.MaxValue); else if (uint.TryParse(tb.Text, out var v)) rec.SetTime(l, m, st, ii, v); MarkDirty($"M{mi.number} #{ii + 1}"); };
                sb.LostFocus += (_, _) => { if (sb.Text.Length == 0) rec.SetScore(l, m, st, ii, uint.MaxValue); else if (uint.TryParse(sb.Text, out var v)) rec.SetScore(l, m, st, ii, v); MarkDirty($"M{mi.number} #{ii + 1}"); };
                Grid.SetRow(tb, i + 1); Grid.SetColumn(tb, 1); g.Children.Add(tb);
                Grid.SetRow(sb, i + 1); Grid.SetColumn(sb, 2); g.Children.Add(sb);
            }
            host.Children.Add(g);
        }
        lvl.SelectionChanged += (_, _) => Build();
        sty.SelectionChanged += (_, _) => Build();
        Detail.Children.Add(Muted(L("Difficulty"), 11)); Detail.Children.Add(lvl);
        Detail.Children.Add(Muted(L("Ace style"), 11)); Detail.Children.Add(sty);
        Detail.Children.Add(host);
        Build();
    }

    void PageCampaignLog()
    {
        var c = cm!; var log = c.Progress;
        var panel = new StackPanel();
        var intro = Muted(L("One row per finished campaign mission, as the ending credits list them. Clearing a mission in the game overwrites its row.")); intro.MaxWidth = 760; intro.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(intro);
        var start = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var sl = Muted(L("Starting ace style gauge")); sl.Width = 220; start.Children.Add(sl);
        var startBox = IntBox(log.StartGauge, L("Starting ace style gauge"), v => log.StartGauge = v, 80); startBox.HorizontalAlignment = HorizontalAlignment.Left;
        start.Children.Add(startBox);
        panel.Children.Add(start);

        var names = aircraft.OrderBy(a => a.index).Select(a => a.name).ToList();
        var g = new Grid();
        string[] heads = { "Mission", "Aircraft", "Score", "Time (s)", "Gauge after", "Rank", "Style" };
        double[] widths = { 0, 200, 90, 80, 80, 60, 120 };
        for (int k = 0; k < heads.Length; k++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[k] == 0 ? GridLength.Auto : new GridLength(widths[k]) });
        g.RowDefinitions.Add(new RowDefinition());
        for (int k = 0; k < heads.Length; k++) { var h = Cell(L(heads[k]), true); Grid.SetColumn(h, k); g.Children.Add(h); }
        foreach (var mi in missions)
        {
            int m = mi.index, row = m + 1;
            g.RowDefinitions.Add(new RowDefinition());
            var name = Cell($"{mi.number} {mi.name}"); name.ToolTip = name.Text; Grid.SetRow(name, row); g.Children.Add(name);
            if (m >= c.CurrentMission && log.Aircraft(m) == 0 && log.Score(m) == 0 && log.TimeSeconds(m) == 0)
            {
                var none = Cell(L("not played yet"), true); Grid.SetRow(none, row); Grid.SetColumn(none, 1); Grid.SetColumnSpan(none, 6); g.Children.Add(none);
                continue;
            }
            var ac = new ComboBox { ItemsSource = names, SelectedIndex = Math.Clamp(log.Aircraft(m), 0, names.Count - 1), Margin = new Thickness(2, 1, 2, 1) };
            ac.SelectionChanged += (_, _) => { if (ac.SelectedIndex >= 0) { log.SetAircraft(m, ac.SelectedIndex); MarkDirty($"M{mi.number} {L("Aircraft")}"); } };
            var rank = new ComboBox { ItemsSource = RankNames.Take(4).ToList(), SelectedIndex = Math.Clamp(log.Rank(m), 0, 3), Margin = new Thickness(2, 1, 2, 1) };
            rank.SelectionChanged += (_, _) => { if (rank.SelectedIndex >= 0) { log.SetRank(m, rank.SelectedIndex); MarkDirty($"M{mi.number} {L("Rank")}"); } };
            var style = new ComboBox { ItemsSource = StyleNames.Select(L).ToList(), SelectedIndex = Math.Clamp(log.Style(m), 0, 2), Margin = new Thickness(2, 1, 2, 1) };
            style.SelectionChanged += (_, _) => { if (style.SelectedIndex >= 0) { log.SetStyle(m, style.SelectedIndex); MarkDirty($"M{mi.number} {L("Style")}"); } };
            UIElement[] cells =
            {
                ac,
                IntBox(log.Score(m), $"M{mi.number} {L("Score")}", v => log.SetScore(m, v)),
                IntBox(log.TimeSeconds(m), $"M{mi.number} {L("Time (s)")}", v => log.SetTimeSeconds(m, v)),
                IntBox(log.GaugeAfter(m), $"M{mi.number} {L("Gauge after")}", v => log.SetGaugeAfter(m, v)),
                rank, style,
            };
            for (int k = 0; k < cells.Length; k++) { Grid.SetRow(cells[k], row); Grid.SetColumn(cells[k], k + 1); g.Children.Add(cells[k]); }
        }
        panel.Children.Add(Wide(g));
        PageHost.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel };
    }

    void PageFlightData()
    {
        var c = cm!; var p = c.Planes;
        var panel = new StackPanel();
        var totals = new TextBlock { Foreground = Res("Fg"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        int Sum(int table) => Enumerable.Range(0, Campaign.Aircraft.Count).Sum(a => p.Stat(table, a));
        void UpdateTotals()
        {
            int player = Sum(Campaign.Aircraft.StatKills), pixy = Sum(Campaign.Aircraft.StatPixyKills), pj = Sum(Campaign.Aircraft.StatPjKills);
            totals.Text = $"{L("Total flight time")}: {FormatTime(c.Records.PlayTimeSeconds)}    {L("Total targets destroyed")}: {player + pixy + pj}" + Environment.NewLine +
                $"{L("Player")}: {player} ({Sum(Campaign.Aircraft.StatAirKills)} + {Sum(Campaign.Aircraft.StatGroundKills)}), {L("Sorties")} {Sum(Campaign.Aircraft.StatSorties)}    " +
                $"Pixy: {pixy} ({Sum(Campaign.Aircraft.StatPixyAirKills)} + {Sum(Campaign.Aircraft.StatPixyGroundKills)}), {L("Sorties")} {Sum(Campaign.Aircraft.StatPixySorties)}    " +
                $"PJ: {pj} ({Sum(Campaign.Aircraft.StatPjAirKills)} + {Sum(Campaign.Aircraft.StatPjGroundKills)}), {L("Sorties")} {Sum(Campaign.Aircraft.StatPjSorties)}";
        }
        var intro = Muted(L("Per-aircraft counters behind Ace Style > Flight Data and the hangar's Aircraft Data. Targets destroyed is air + ground and updates on its own. Usage is the aircraft's share of all player sorties. The game does not store flight time per aircraft.")); intro.MaxWidth = 760; intro.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(intro);
        panel.Children.Add(totals);

        Grid Table(string title, int sortiesT, int airT, int groundT, int killsT, bool usage)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            string[] heads = usage ? new[] { "Aircraft", "Sorties", "Air", "Ground", "Targets destroyed", "Usage" } : new[] { "Aircraft", "Sorties", "Air", "Ground", "Targets destroyed" };
            double[] widths = { 220, 80, 80, 80, 120, 80 };
            for (int k = 0; k < heads.Length; k++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[k]) });
            g.RowDefinitions.Add(new RowDefinition());
            for (int k = 0; k < heads.Length; k++) { var h = Cell(L(heads[k]), true); Grid.SetColumn(h, k); g.Children.Add(h); }
            var usageCells = new List<(int ac, TextBlock cell)>();
            void RefreshUsage()
            {
                int all = Sum(sortiesT);
                foreach (var (ac, cell) in usageCells) cell.Text = all > 0 ? $"{100.0 * p.Stat(sortiesT, ac) / all:0.00}%" : "0.00%";
            }
            int row = 1;
            foreach (var ac in aircraft.OrderBy(a => a.order))
            {
                int i = ac.index;
                g.RowDefinitions.Add(new RowDefinition());
                var name = Cell(ac.name); Grid.SetRow(name, row); g.Children.Add(name);
                var kills = Cell(p.Stat(killsT, i).ToString());
                void Recount() { p.SetStat(killsT, i, p.Stat(airT, i) + p.Stat(groundT, i)); kills.Text = p.Stat(killsT, i).ToString(); UpdateTotals(); }
                var sorties = IntBox(p.Stat(sortiesT, i), $"{ac.name} {title} {L("Sorties")}", v => { p.SetStat(sortiesT, i, Math.Max(0, v)); RefreshUsage(); UpdateTotals(); });
                var air = IntBox(p.Stat(airT, i), $"{ac.name} {title} {L("Air")}", v => { p.SetStat(airT, i, Math.Max(0, v)); Recount(); });
                var ground = IntBox(p.Stat(groundT, i), $"{ac.name} {title} {L("Ground")}", v => { p.SetStat(groundT, i, Math.Max(0, v)); Recount(); });
                UIElement[] cells = { sorties, air, ground, kills };
                for (int k = 0; k < cells.Length; k++) { Grid.SetRow(cells[k], row); Grid.SetColumn(cells[k], k + 1); g.Children.Add(cells[k]); }
                if (usage) { var u = Cell(""); Grid.SetRow(u, row); Grid.SetColumn(u, 5); g.Children.Add(u); usageCells.Add((i, u)); }
                row++;
            }
            RefreshUsage();
            return g;
        }

        panel.Children.Add(Label(L("Player"), 15, true));
        panel.Children.Add(Wide(Table(L("Player"), Campaign.Aircraft.StatSorties, Campaign.Aircraft.StatAirKills, Campaign.Aircraft.StatGroundKills, Campaign.Aircraft.StatKills, true)));
        panel.Children.Add(Label("Pixy", 15, true));
        panel.Children.Add(Muted(L("Indexed by the wingman's aircraft. Pixy flies campaign missions 1-12.")));
        panel.Children.Add(Wide(Table("Pixy", Campaign.Aircraft.StatPixySorties, Campaign.Aircraft.StatPixyAirKills, Campaign.Aircraft.StatPixyGroundKills, Campaign.Aircraft.StatPixyKills, false)));
        panel.Children.Add(Label("PJ", 15, true));
        panel.Children.Add(Muted(L("Indexed by the wingman's aircraft. PJ flies campaign missions 13-17.")));
        panel.Children.Add(Wide(Table("PJ", Campaign.Aircraft.StatPjSorties, Campaign.Aircraft.StatPjAirKills, Campaign.Aircraft.StatPjGroundKills, Campaign.Aircraft.StatPjKills, false)));
        UpdateTotals();
        PageHost.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel };
    }

    void PageMedals()
    {
        var c = cm!;
        var items = new List<CardItem>();
        foreach (var md in medals)
        {
            int i = md.index;
            var it = new CardItem
            {
                Title = $"{L("Medal")} {i + 1}", Sub = md.description, SearchText = md.description + " " + md.condition,
                Get = () => c.Rivals.Medal(i), Set = v => c.Rivals.SetMedal(i, v),
            };
            it.ShowDetail = () => ShowDetailPanel(null, 0, $"{L("Medal")} {i + 1}", (L("Description"), md.description), (L("Condition"), md.condition), (L("Index"), i.ToString()));
            items.Add(it);
        }
        PageHost.Content = CardGrid(items, L("medals"), L("Awarded"), L("Not awarded"));
    }

    void PageAces()
    {
        var c = cm!;
        var items = new List<CardItem>();
        foreach (var rv in rivals)
        {
            int i = rv.index;
            var it = new CardItem
            {
                Title = $"{L("Ace")} {i + 1}", Sub = rv.fate, SearchText = rv.bio + " " + rv.fate,
                Get = () => c.Rivals.Destroyed(i), Set = v => c.Rivals.SetDestroyed(i, v),
            };
            it.Options.Add(new CardOption(L("[NEW] marker"), L("Shown in Assault Records until the entry is opened"), () => c.Rivals.IsNew(i), v => c.Rivals.SetNew(i, v)));
            it.ShowDetail = () => ShowDetailPanel(null, 0, $"{L("Ace")} {i + 1}", (L("Record"), rv.fate), (L("Profile"), rv.bio), (L("Index"), i.ToString()));
            items.Add(it);
        }
        PageHost.Content = CardGrid(items, L("aces"), L("Shot down"), L("Not yet"));
    }
}
