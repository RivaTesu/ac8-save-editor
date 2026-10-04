using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ac8Save;

namespace Ac8SaveEditor;

public partial class MainWindow : Window
{
    static readonly string DefaultSaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BANDAI NAMCO Entertainment", "ACE COMBAT 8", "Saved", "SaveGames");
    static string SaveDir = LoadSaveDir();
    static string SaveDirFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ac8SaveEditor", "savedir.txt");
    static string LoadSaveDir() { try { var v = File.ReadAllText(SaveDirFile).Trim(); if (Directory.Exists(v)) return v; } catch { } return DefaultSaveDir; }

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L("Select the SaveGames folder"), InitialDirectory = Directory.Exists(SaveDir) ? SaveDir : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        if (dlg.ShowDialog() != true) return;
        if (dirty && MessageBox.Show(L("Discard unsaved changes?"), "AC8", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SaveDir = dlg.FolderName;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SaveDirFile)!); File.WriteAllText(SaveDirFile, SaveDir); } catch { }
        LoadSaves();
        ShowPage(page);
    }

    static readonly string[] DifficultyNames = { "None", "Casual", "Easy", "Normal", "Hard", "Ace", "FreeFlight" };
    static readonly string[] DiffKeys = { "", "CampaignDifficulty_Select_VeryEasy", "CampaignDifficulty_Select_Easy", "CampaignDifficulty_Select_Normal", "CampaignDifficulty_Select_Hard", "CampaignDifficulty_Select_Ace" };
    // In-game difficulty name (Rookie/Pilot/Veteran/Elite/Ace), title case, from the game text tables.
    string Dn(int level)
    {
        if (level < 1 || level >= DiffKeys.Length) return DifficultyNames[Math.Clamp(level, 0, 6)];
        var t = data.T(DiffKeys[level], DifficultyNames[level]);
        int p = t.IndexOf(" (");
        if (p > 0) t = t[..p];
        return t.Length > 1 ? t[0] + t[1..].ToLowerInvariant() : t;
    }
    static readonly string[] RankNames = { "None", "G", "F", "E", "D", "C", "B", "A", "S" };
    static readonly string[] FeatureNames = { "None", "AceDifficulty", "AircraftSet", "AircraftTree", "Skin", "Emblem", "Part", "Training", "MusicPlayer", "SpWeapon2ndSlot", "Weathering", "FreeMission", "FreeFlight", "DataViewer" };

    GameData data;
    SaveFile? campaignFile, systemFile;
    CampaignModel? cm;
    bool dirty;
    string page = "overview";

    public MainWindow()
    {
        InitializeComponent();
        Lang = LoadLang();
        data = Current = new GameData(Path.Combine(AppContext.BaseDirectory, "assets"), Lang);
        ApplyUiLanguage();
        LoadSaves();
        Nav.SelectedIndex = 0;
    }

    // ---------- language ----------

    public static string Lang = "en";
    static readonly Dictionary<string, string> PtStrings = LoadPt();
    static Dictionary<string, string> LoadPt()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "strings.pt.json");
        if (!File.Exists(p)) return new();
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new();
    }
    public static string L(string en) => Lang == "pt" && PtStrings.TryGetValue(en, out var pt) ? pt : en;
    static string LangFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ac8SaveEditor", "lang.txt");
    static string LoadLang() { try { var v = File.ReadAllText(LangFile).Trim(); return v == "pt" ? "pt" : "en"; } catch { return "en"; } }
    static void SaveLang(string v) { try { Directory.CreateDirectory(Path.GetDirectoryName(LangFile)!); File.WriteAllText(LangFile, v); } catch { } }

    void ApplyUiLanguage()
    {
        CmbLang.SelectionChanged -= Lang_Changed;
        CmbLang.SelectedIndex = Lang == "pt" ? 1 : 0;
        CmbLang.SelectionChanged += Lang_Changed;
        BtnReload.Content = L("Reload");
        BtnBrowse.Content = L("Save folder...");
        BtnSave.Content = L("Save (with backup)");
        LblSearch.Text = L("Search:");
        BtnAll.Content = L("Check all");
        BtnNone.Content = L("Uncheck all");
        var navNames = new[] { "Overview", "Missions", "Aircraft", "Skins", "Emblems", "Medals", "Parts", "Assault records", "Features and flags", "Options (System.sav)", "Advanced (tree)" };
        for (int i = 0; i < Nav.Items.Count && i < navNames.Length; i++) ((ListBoxItem)Nav.Items[i]).Content = L(navNames[i]);
    }

    void Lang_Changed(object s, SelectionChangedEventArgs e)
    {
        Lang = CmbLang.SelectedIndex == 1 ? "pt" : "en";
        SaveLang(Lang);
        data = Current = new GameData(Path.Combine(AppContext.BaseDirectory, "assets"), Lang);
        ApplyUiLanguage();
        ShowPage(page);
    }

    // ---------- files ----------

    void LoadSaves()
    {
        campaignFile = systemFile = null; cm = null;
        var problems = new List<string>();
        foreach (var (name, setter) in new (string, Action<SaveFile>)[] { ("Campaign.sav", f => campaignFile = f), ("System.sav", f => systemFile = f) })
        {
            var p = Path.Combine(SaveDir, name);
            if (!File.Exists(p)) { problems.Add($"{name} {L("not found")}"); continue; }
            try
            {
                var bytes = File.ReadAllBytes(p);
                var sf = Reader.Load(bytes);
                var re = Writer.Save(sf, Checksum.Compute);
                if (!re.AsSpan().SequenceEqual(bytes)) problems.Add($"{name}: round-trip divergente (checksum ou formato)");
                setter(sf);
            }
            catch (Exception ex) { problems.Add($"{name}: {ex.Message}"); }
        }
        if (campaignFile != null) cm = new CampaignModel(campaignFile);
        dirty = false;
        BtnSave.IsEnabled = campaignFile != null || systemFile != null;
        LblFile.Text = SaveDir;
        LblStatus.Text = problems.Count == 0 ? L("Campaign.sav and System.sav loaded. Checksums valid.") : string.Join(" | ", problems);
    }

    void Reload_Click(object s, RoutedEventArgs e)
    {
        if (dirty && MessageBox.Show(L("Discard unsaved changes?"), "AC8", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        LoadSaves();
        ShowPage(page);
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        var bak = Path.Combine(SaveDir, "backup_ac8edit");
        Directory.CreateDirectory(bak);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var saved = new List<string>();
        try
        {
            foreach (var (name, sf) in new (string, SaveFile?)[] { ("Campaign.sav", campaignFile), ("System.sav", systemFile) })
            {
                if (sf == null) continue;
                var p = Path.Combine(SaveDir, name);
                File.Copy(p, Path.Combine(bak, $"{Path.GetFileNameWithoutExtension(name)}_{stamp}.sav"), true);
                File.WriteAllBytes(p, Writer.Save(sf, Checksum.Compute));
                saved.Add(name);
            }
            dirty = false;
            LblStatus.Text = L($"Saved: {string.Join(", ", saved)}. Backup in {bak}");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, L("Save failed"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void MarkDirty(string what)
    {
        dirty = true;
        LblStatus.Text = L($"Changed: {what} (unsaved)");
    }

    // ---------- navigation ----------

    void Nav_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem li && li.Tag is string tag) ShowPage(tag);
    }

    string search = "";
    string filter = "";
    void Search_TextChanged(object s, TextChangedEventArgs e) { search = TxtSearch.Text.Trim(); refresh?.Invoke(); }
    void Filter_Changed(object s, SelectionChangedEventArgs e) { filter = CmbFilter.SelectedItem as string ?? ""; refresh?.Invoke(); }

    Action? refresh;
    Action<bool>? setAll;

    void UnlockAll_Click(object s, RoutedEventArgs e) => setAll?.Invoke(true);
    void LockAll_Click(object s, RoutedEventArgs e) => setAll?.Invoke(false);

    void ShowPage(string tag)
    {
        page = tag;
        refresh = null; setAll = null;
        CmbFilter.ItemsSource = null; CmbFilter.Visibility = Visibility.Collapsed;
        BtnAll.Visibility = BtnNone.Visibility = Visibility.Collapsed;
        LblPageInfo.Text = "";
        Detail.Children.Clear();
        Detail.Children.Add(Muted(L("Select an item to see details.")));
        if (cm == null && tag != "system" && tag != "raw")
        {
            LblPageTitle.Text = L("Campaign.sav not loaded");
            PageHost.Content = Muted(L("Put the save in ") + SaveDir);
            return;
        }
        switch (tag)
        {
            case "overview": PageOverview(); break;
            case "missions": PageMissions(); break;
            case "aircraft": PageAircraft(); break;
            case "skins": PageSkins(); break;
            case "emblems": PageEmblems(); break;
            case "medals": PageMedals(); break;
            case "parts": PageParts(); break;
            case "assault": PageAssault(); break;
            case "features": PageFeatures(); break;
            case "system": PageSystem(); break;
            case "raw": PageRaw(); break;
        }
    }

    // ---------- helpers ----------

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static TextBlock Muted(string t, double size = 13) => new() { Text = t, Foreground = Res("Fg2"), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
    static TextBlock Label(string t, double size = 13, bool bold = false) => new() { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    static readonly Dictionary<string, BitmapImage> imgCache = new();
    static GameData? Current;
    static BitmapImage? Img(string? path, int decodeWidth)
    {
        if (path == null) return null;
        var key = path + "#" + decodeWidth;
        if (imgCache.TryGetValue(key, out var b)) return b;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            var bytes = Current?.ImageBytes(path);
            if (bytes == null) return null;
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

    static Image ImageBox(string? path, double w, double h, int decode)
    {
        var img = new Image { Width = w, Height = h, Stretch = Stretch.Uniform, Source = Img(path, decode) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    sealed class CardItem
    {
        public string Title = ""; public string Sub = ""; public string? Image; public double ImgW = 96, ImgH = 96; public int Decode = 160;
        public Func<bool> Get = () => false; public Action<bool> Set = _ => { };
        public Action? ShowDetail; public string SearchText = ""; public string Group = "";
        // Extra per-card switches. They are enabled only while the main switch is on.
        public List<CardOption> Options = new();
        public string Warning = ""; public string WarningTip = "";
    }

    sealed record CardOption(string Label, string Tip, Func<bool> Get, Action<bool> Set);

    bool Visible(CardItem it) =>
        (search.Length == 0 || it.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase) || it.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
        && (filter.Length == 0 || filter == L("All") || it.Group == filter);

    ScrollViewer CardGrid(List<CardItem> items, string what)
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
                var border = new Border { Style = (Style)Application.Current.Resources["Card"], Width = 200, Cursor = System.Windows.Input.Cursors.Hand };
                var stack = new StackPanel();
                var img = ImageBox(it.Image, it.ImgW, it.ImgH, it.Decode);
                img.Opacity = state ? 1 : 0.35;
                stack.Children.Add(img);
                var title = Label(it.Title, 12, true); title.TextTrimming = TextTrimming.CharacterEllipsis; title.MaxHeight = 36;
                stack.Children.Add(title);
                if (it.Sub.Length > 0) { var sub = Muted(it.Sub, 11); sub.TextTrimming = TextTrimming.CharacterEllipsis; sub.MaxHeight = 32; stack.Children.Add(sub); }
                if (it.Warning.Length > 0) { var warn = Muted(it.Warning, 11); warn.Foreground = Res("Bad"); warn.ToolTip = it.WarningTip; stack.Children.Add(warn); }
                var chk = new CheckBox { Content = state ? L("Unlocked") : L("Locked"), IsChecked = state, Margin = new Thickness(0, 4, 0, 0), Foreground = state ? Res("Ok") : Res("Fg2") };
                chk.Checked += (_, _) => { it.Set(true); MarkDirty(it.Title); Build(); };
                chk.Unchecked += (_, _) => { it.Set(false); MarkDirty(it.Title); Build(); };
                stack.Children.Add(chk);
                foreach (var opt in it.Options)
                {
                    bool optOn = opt.Get();
                    var oc = new CheckBox { Content = opt.Label, ToolTip = opt.Tip, IsChecked = optOn, IsEnabled = state, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Foreground = optOn ? Res("Fg") : Res("Fg2") };
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
            foreach (var it in items) if (Visible(it)) it.Set(v);
            MarkDirty(v ? L("check all") : L("uncheck all"));
            Build();
        };
        BtnAll.Visibility = BtnNone.Visibility = Visibility.Visible;
        Build();
        return sv;
    }

    void SetFilterGroups(IEnumerable<string> groups)
    {
        var list = new List<string> { L("All") };
        list.AddRange(groups.Distinct().OrderBy(g => g));
        filter = "";
        CmbFilter.ItemsSource = list; CmbFilter.SelectedIndex = 0; CmbFilter.Visibility = Visibility.Visible;
    }

    void ShowDetailPanel(string? bigImage, double imgH, string title, params (string label, string value)[] rows)
    {
        Detail.Children.Clear();
        if (bigImage != null) { var img = ImageBox(bigImage, 320, imgH, 640); img.HorizontalAlignment = HorizontalAlignment.Left; Detail.Children.Add(img); }
        Detail.Children.Add(Label(title, 16, true));
        foreach (var (l, v) in rows)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            Detail.Children.Add(Muted(l, 11));
            Detail.Children.Add(Label(v));
        }
    }

    // ---------- pages ----------

    void PageOverview()
    {
        LblPageTitle.Text = L("Overview");
        var m = cm!;
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var grid = new WrapPanel();
        void Stat(string label, string value)
        {
            var b = new Border { Style = (Style)Application.Current.Resources["Card"], Width = 220, Padding = new Thickness(14) };
            var st = new StackPanel(); st.Children.Add(Muted(label, 11)); st.Children.Add(Label(value, 20, true)); b.Child = st; grid.Children.Add(b);
        }
        var secs = TimeSpan.FromSeconds(m.Int("TotalPlayTimeSec"));
        Stat(L("Play time"), $"{(int)secs.TotalHours}h {secs.Minutes:00}m");
        Stat(L("Current MRP"), m.Int("CurrentMRP").ToString("N0"));
        Stat(L("Total MRP"), m.Int("TotalMRP").ToString("N0"));
        Stat(L("Saved difficulty"), Dn(Array.IndexOf(DifficultyNames, m.EnumOf("SavedDifficulty").Replace("ELiveDifficulty::", ""))));
        Stat(L("Missions with records"), m.Missions().Count.ToString());
        Stat(L("Campaign clears"), m.Int("CompletionCount").ToString());
        Stat(L("Aircraft"), m.OwnedAircraft().Count.ToString());
        Stat(L("Skins"), m.U32List("UnlockedSkinIdList").Count.ToString());
        Stat(L("Emblems"), m.U32List("UnlockedEmblemIdList").Count.ToString());
        Stat(L("Medals"), m.U32List("UnlockedMedalIdList").Count.ToString());
        Stat(L("Parts"), m.U32List("OwnedParts").Count.ToString());
        Stat(L("Ace unlocked"), m.HasFeature(1) ? L("Yes") : L("No"));
        sp.Children.Add(grid);

        sp.Children.Add(Label(L("Edit values"), 15, true));
        var form = new Grid { Margin = new Thickness(6, 4, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        void NumField(string label, string prop)
        {
            form.RowDefinitions.Add(new RowDefinition());
            var l = Label(label); l.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            var tb = new TextBox { Text = m.Int(prop).ToString(), Margin = new Thickness(0, 3, 0, 3) }; Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
            var btn = new Button { Content = L("Apply"), Margin = new Thickness(8, 3, 0, 3) }; Grid.SetRow(btn, row); Grid.SetColumn(btn, 2);
            btn.Click += (_, _) => { if (long.TryParse(tb.Text, out var v)) { m.SetInt(prop, v); MarkDirty(label); ShowPage("overview"); } };
            form.Children.Add(l); form.Children.Add(tb); form.Children.Add(btn); row++;
        }
        NumField(L("Current MRP"), "CurrentMRP");
        NumField(L("Total MRP"), "TotalMRP");
        NumField(L("Play time (seconds)"), "TotalPlayTimeSec");
        NumField(L("Campaign clears"), "CompletionCount");
        NumField(L("Aircraft set slots"), "OwnedAircraftSetSlotCount");
        form.RowDefinitions.Add(new RowDefinition());
        var dl = Label(L("Saved difficulty")); dl.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(dl, row); Grid.SetColumn(dl, 0);
        var cb = new ComboBox { ItemsSource = Enumerable.Range(1, 5).Select(Dn).ToList(), Margin = new Thickness(0, 3, 0, 3) };
        cb.SelectedIndex = Array.IndexOf(DifficultyNames.Skip(1).Take(5).ToArray(), m.EnumOf("SavedDifficulty").Replace("ELiveDifficulty::", ""));
        Grid.SetRow(cb, row); Grid.SetColumn(cb, 1);
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { m.SetEnum("SavedDifficulty", "ELiveDifficulty::" + DifficultyNames[cb.SelectedIndex + 1]); MarkDirty(L("difficulty")); } };
        form.Children.Add(dl); form.Children.Add(cb);
        sp.Children.Add(form);

        sp.Children.Add(Label(L("Shortcuts"), 15, true));
        var quick = new WrapPanel();
        var ace = new Button { Content = L("Unlock Ace difficulty"), Margin = new Thickness(0, 4, 8, 4) };
        ace.Click += (_, _) => { UnlockAce(); ShowPage("overview"); };
        quick.Children.Add(ace);
        var allHard = new Button { Content = $"{L("All missions cleared on")} {Dn(4)} ({L("S rank")})", Margin = new Thickness(0, 4, 8, 4) };
        allHard.Click += (_, _) => { CompleteAll(4); ShowPage("overview"); };
        quick.Children.Add(allHard);
        var allAce = new Button { Content = $"{L("All missions cleared on")} {Dn(5)} ({L("S rank")})", Margin = new Thickness(0, 4, 8, 4) };
        allAce.Click += (_, _) => { CompleteAll(5); ShowPage("overview"); };
        quick.Children.Add(allAce);
        sp.Children.Add(quick);
        PageHost.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    void UnlockAce()
    {
        var m = cm!;
        m.SetFeature(1, true);
        foreach (var u in m.UnlockEntries()) if (u.Id == 1800001) u.Activated.V = true;
        m.SetMenuMiscFlag("ELiveMenuMiscFlagID::NewAceDifficulty", true);
        MarkDirty(L("Ace unlocked"));
    }

    void CompleteAll(byte level)
    {
        var m = cm!;
        foreach (var rec in m.Missions())
        {
            var d = rec.Difficulties.FirstOrDefault(x => x.Level?.V == level);
            if (d == null) m.AddDifficulty(rec, level);
            else if (d.HighestRank != null) d.HighestRank.V = "ELiveClearRank::S";
        }
        MarkDirty($"{L("missions on")} {Dn(level)}");
    }

    void PageMissions()
    {
        LblPageTitle.Text = L("Missions");
        var m = cm!;
        var recs = m.Missions();
        var sp = new StackPanel();
        foreach (var mi in data.Missions.Where(x => x.Id >= 1 && x.Id <= 31))
        {
            var rec = recs.FirstOrDefault(r => r.MissionId == mi.Id);
            var card = new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(12, 8, 12, 8) };
            var g = new DockPanel();
            var head = new StackPanel { Width = 300 };
            head.Children.Add(Label($"{mi.Number}  {mi.Name}", 14, true));
            head.Children.Add(Muted(mi.Operation, 11));
            head.Children.Add(Muted(mi.Map, 11));
            DockPanel.SetDock(head, Dock.Left);
            g.Children.Add(head);
            var diffs = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            for (byte lvl = 1; lvl <= 5; lvl++)
            {
                var d = rec?.Difficulties.FirstOrDefault(x => x.Level?.V == lvl);
                var cell = new Border { Background = Res(d != null ? "AccentDim" : "Panel2"), CornerRadius = new CornerRadius(4), Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4), Width = 118 };
                var cs = new StackPanel();
                cs.Children.Add(Label(Dn(lvl), 12, true));
                if (d != null)
                {
                    var rank = d.HighestRank?.V.Replace("ELiveClearRank::", "") ?? "-";
                    var rankBox = new ComboBox { ItemsSource = RankNames.Skip(1).Reverse().ToList(), Width = 60, Margin = new Thickness(0, 2, 0, 2) }; rankBox.SelectedIndex = Array.IndexOf(RankNames.Skip(1).Reverse().ToArray(), rank);
                    var dCopy = d; var lvlCopy = lvl;
                    rankBox.SelectionChanged += (_, _) => { if (rankBox.SelectedItem is string r && dCopy.HighestRank != null) { dCopy.HighestRank.V = "ELiveClearRank::" + r; MarkDirty($"{mi.Number} {DifficultyNames[lvlCopy]} rank"); } };
                    cs.Children.Add(rankBox);
                    var t = d.TimeMs != null ? TimeSpan.FromMilliseconds(d.TimeMs.V) : TimeSpan.Zero;
                    cs.Children.Add(Muted($"{t:mm\\:ss}  {d.Score?.V:N0} pts", 10));
                    var rm = new Button { Content = L("remove"), Padding = new Thickness(6, 1, 6, 1), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
                    var recCopy = rec!;
                    rm.Click += (_, _) => { m.RemoveDifficulty(recCopy, dCopy); MarkDirty($"{mi.Number} {DifficultyNames[lvlCopy]} removido"); PageMissions(); };
                    cs.Children.Add(rm);
                }
                else
                {
                    cs.Children.Add(Muted(L("not cleared"), 10));
                    var add = new Button { Content = L("clear (S)"), Padding = new Thickness(6, 1, 6, 1), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
                    var lvlCopy = lvl;
                    add.Click += (_, _) =>
                    {
                        if (rec == null) { MessageBox.Show(L("This mission has no record in the save. Clear it once on any difficulty."), "AC8"); return; }
                        m.AddDifficulty(rec, lvlCopy); MarkDirty($"{mi.Number} {DifficultyNames[lvlCopy]} {L("cleared")}"); PageMissions();
                    };
                    cs.Children.Add(add);
                }
                cell.Child = cs;
                diffs.Children.Add(cell);
            }
            g.Children.Add(diffs);
            card.Child = g;
            var miCopy = mi;
            card.MouseLeftButtonUp += (_, _) => ShowDetailPanel(null, 0, $"{miCopy.Number} {miCopy.Name}", (L("Operation"), miCopy.Operation), (L("Location"), miCopy.Map), (L("Objective"), miCopy.Objective), ("ID", miCopy.Id.ToString()));
            sp.Children.Add(card);
        }
        LblPageInfo.Text = $"{recs.Count} {L("missions with records")}";
        PageHost.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    void PageAircraft()
    {
        LblPageTitle.Text = L("Aircraft");
        var m = cm!;
        var items = new List<CardItem>();
        foreach (var a in data.Aircraft)
        {
            var ac = a;
            var skin = data.Skins.FirstOrDefault(s => s.Id == ac.DefaultSkinId);
            var category = "EPlaneCategory::" + ac.Category;
            string WeaponName(string id) => data.Weapons.TryGetValue(id, out var w) ? w.ShortName : id.Replace("ELiveWeaponID::WID_", "");
            var item = new CardItem
            {
                Title = ac.Name, Sub = $"{ac.Category}  ·  {ac.Cost:N0} MRP", Image = skin?.Banner ?? ac.Icon, ImgW = 120, ImgH = 150, Decode = 240,
                Group = ac.Category, SearchText = ac.Name + " " + ac.Nickname + " " + ac.ShortId,
                Get = () => m.OwnedAircraft().ContainsKey(ac.Id),
                Set = v =>
                {
                    m.SetOwnedAircraft(ac.Id, v);
                    m.SetContains("UnlockedAircraftTreeNodeIDs", ac.Id, v, "NewlyUnlockedAircraftTreeNodeIDs");
                    // Owning: the game gives the slot 1 weapon with the aircraft. Removing: the game keeps the record with no weapons.
                    if (v && ac.SpWeapons.Count > 0) m.SetOwnedWeapon(ac.Id, ac.SpWeapons[0], true, category, data.WeaponOrder);
                    if (!v) foreach (var w in m.OwnedWeapons(ac.Id)) m.SetOwnedWeapon(ac.Id, w, false);
                },
                ShowDetail = () => ShowDetailPanel(skin?.Banner ?? ac.Icon, 420, ac.Name, (L("Nickname"), ac.Nickname), (L("Category"), ac.Category), (L("Cost"), ac.Cost.ToString("N0") + " MRP"), ("ID", ac.Id.ToString()),
                    (L("Copies owned (you + wingmen)"), m.OwnedAircraft().TryGetValue(ac.Id, out var f) ? $"{f} / 4" : "-"),
                    (L("Special weapons"), string.Join("\n", ac.SpWeapons.Select((w, i) => $"SP{i + 1}: {(data.Weapons.TryGetValue(w, out var wi) ? wi.Name : w)}{(m.OwnedWeapons(ac.Id).Contains(w) ? "" : $" ({L("locked")})")}"))),
                    (L("Description"), ac.Description)),
            };
            if (ac.MissionOnly)
            {
                item.Warning = L("Mission only, not in the aircraft tree");
                item.WarningTip = ac.LoanMission > 0 ? string.Format(L("Loaned for Mission {0}. The aircraft tree does not sell it."), ac.LoanMission) : L("Loaned for one mission. The aircraft tree does not sell it.");
            }
            for (int i = 0; i < ac.SpWeapons.Count; i++)
            {
                var wid = ac.SpWeapons[i];
                item.Options.Add(new CardOption($"SP{i + 1}  {WeaponName(wid)}", data.Weapons.TryGetValue(wid, out var wi) ? wi.Name : wid,
                    () => m.OwnedWeapons(ac.Id).Contains(wid),
                    v => m.SetOwnedWeapon(ac.Id, wid, v, category, data.WeaponOrder)));
            }
            items.Add(item);
        }
        SetFilterGroups(items.Select(i => i.Group));
        PageHost.Content = CardGrid(items, L("aircraft"));
    }

    void PageSkins()
    {
        LblPageTitle.Text = L("Skins");
        var m = cm!;
        var items = new List<CardItem>();
        foreach (var s in data.Skins)
        {
            var sk = s;
            var plane = data.Aircraft.FirstOrDefault(a => a.ShortId == sk.PlaneStringId);
            var planeName = plane?.Name ?? sk.PlaneStringId;
            items.Add(new CardItem
            {
                Title = sk.Name.Replace("{AircraftName}", planeName), Sub = planeName, Image = sk.Banner ?? sk.Icon, ImgW = 120, ImgH = 150, Decode = 240,
                Group = planeName, SearchText = sk.Name + " " + planeName + " " + sk.Id,
                Get = () => m.Contains("UnlockedSkinIdList", sk.Id),
                Set = v => m.SetContains("UnlockedSkinIdList", sk.Id, v, "NewlyUnlockedSkinIdList"),
                ShowDetail = () => ShowDetailPanel(sk.Banner ?? sk.Icon, 420, sk.Name.Replace("{AircraftName}", planeName), (L("Aircraft"), planeName), (L("Category"), sk.Category), ("ID", sk.Id.ToString()), (L("Description"), sk.Description)),
            });
        }
        SetFilterGroups(items.Select(i => i.Group));
        PageHost.Content = CardGrid(items, L("skins"));
    }

    void PageEmblems()
    {
        LblPageTitle.Text = L("Emblems");
        var m = cm!;
        var items = new List<CardItem>();
        foreach (var e in data.Emblems)
        {
            var em = e;
            items.Add(new CardItem
            {
                Title = em.Name, Sub = em.Category, Image = em.Icon, ImgW = 110, ImgH = 110, Decode = 220,
                Group = em.Category, SearchText = em.Name + " " + em.Id,
                Get = () => m.Contains("UnlockedEmblemIdList", em.Id),
                Set = v => m.SetContains("UnlockedEmblemIdList", em.Id, v, "NewlyUnlockedEmblemIdList"),
                ShowDetail = () => ShowDetailPanel(em.Icon, 320, em.Name, (L("Category"), em.Category), ("ID", em.Id.ToString()), (L("Description"), em.Description)),
            });
        }
        SetFilterGroups(items.Select(i => i.Group));
        PageHost.Content = CardGrid(items, L("emblems"));
    }

    void PageMedals()
    {
        LblPageTitle.Text = L("Medals");
        var m = cm!;
        var items = new List<CardItem>();
        foreach (var md in data.Medals)
        {
            var me = md;
            var rule = data.UnlockRules.Values.FirstOrDefault(r => r.Actions.Any(a => a.StartsWith("UnlockMedal") && a.Contains($"Id:{me.Id},")));
            items.Add(new CardItem
            {
                Title = me.Name, Sub = me.Hint, Image = me.Icon, ImgW = 90, ImgH = 135, Decode = 200,
                SearchText = me.Name + " " + me.Description,
                Get = () => m.Contains("UnlockedMedalIdList", me.Id),
                Set = v => m.SetContains("UnlockedMedalIdList", me.Id, v, "NewlyUnlockedMedalIdList"),
                ShowDetail = () => ShowDetailPanel(me.Icon, 480, me.Name, (L("How to get"), me.Hint), (L("Description"), me.Description), (L("Internal condition"), rule != null ? string.Join("; ", rule.Conditions) : ""), ("ID", me.Id.ToString())),
            });
        }
        PageHost.Content = CardGrid(items, L("medals"));
    }

    void PageParts()
    {
        LblPageTitle.Text = L("Parts");
        var m = cm!;
        var items = new List<CardItem>();
        foreach (var p in data.Parts)
        {
            var pt = p;
            items.Add(new CardItem
            {
                Title = pt.Name, Sub = $"{pt.Position}  ·  {pt.Cost:N0} MRP", Image = pt.Icon, ImgW = 80, ImgH = 80, Decode = 160,
                Group = pt.Position, SearchText = pt.Name + " " + pt.ShortName + " " + pt.Id,
                Get = () => m.Contains("OwnedParts", pt.Id),
                Set = v => { m.SetContains("OwnedParts", pt.Id, v, "NewlyOwnedParts"); m.SetContains("UnlockedAircraftTreeNodeIDs", pt.Id, v, "NewlyUnlockedAircraftTreeNodeIDs"); },
                ShowDetail = () => ShowDetailPanel(pt.Icon, 160, pt.Name, (L("Short name"), pt.ShortName), (L("Slot"), pt.Position), (L("Type"), pt.Kind), (L("Cost"), pt.Cost.ToString("N0") + " MRP"), ("ID", pt.Id.ToString()), (L("Description"), pt.Description)),
            });
        }
        SetFilterGroups(items.Select(i => i.Group));
        PageHost.Content = CardGrid(items, L("parts"));
    }

    void PageAssault()
    {
        LblPageTitle.Text = L("Assault records");
        var m = cm!;
        var items = new List<CardItem>();
        foreach (var a in data.AssaultRecords)
        {
            var ar = a;
            items.Add(new CardItem
            {
                Title = $"{ar.TacName}  ·  {ar.Name}", Sub = $"{ar.Rank} · {ar.AircraftName}", Image = ar.Icon, ImgW = 100, ImgH = 100, Decode = 200,
                SearchText = ar.Name + " " + ar.TacName + " " + ar.AircraftName + " " + ar.Unit,
                Get = () => m.Contains("OwnedAssaultRecords", ar.Id),
                Set = v => m.SetContains("OwnedAssaultRecords", ar.Id, v, "NewAssaultRecords"),
                ShowDetail = () => ShowDetailPanel(ar.Thumbnail ?? ar.Icon, 320, $"{ar.TacName} · {ar.Name}", (L("Rank"), ar.Rank), (L("Age"), ar.Age.ToString()), (L("Aircraft"), ar.AircraftName), (L("Unit"), ar.Unit), (L("How to get"), ar.Hint), (L("Profile"), ar.Profile), ("ID", ar.Id.ToString())),
            });
        }
        PageHost.Content = CardGrid(items, L("records"));
    }

    void PageFeatures()
    {
        LblPageTitle.Text = L("Features and flags");
        var m = cm!;
        var sp = new StackPanel();
        sp.Children.Add(Label(L("Menu features (FeatureFlagMask)"), 15, true));
        sp.Children.Add(Muted(L("Each bit enables a game feature. AceDifficulty = Ace difficulty.")));
        var wrap = new WrapPanel();
        for (int bit = 1; bit < FeatureNames.Length; bit++)
        {
            var b = bit;
            var chk = new CheckBox { Content = FeatureNames[b], IsChecked = m.HasFeature(b), Width = 180, Margin = new Thickness(6) };
            chk.Checked += (_, _) => { m.SetFeature(b, true); MarkDirty(FeatureNames[b]); };
            chk.Unchecked += (_, _) => { m.SetFeature(b, false); MarkDirty(FeatureNames[b]); };
            wrap.Children.Add(chk);
        }
        sp.Children.Add(wrap);

        sp.Children.Add(Label(L("Menu flags (MenuMiscFlags)"), 15, true));
        var flags = m.MenuMiscFlags();
        var wrap2 = new WrapPanel();
        foreach (var f in new[] { "NewAceDifficulty", "CompletedInitialSetting", "AlreadySeenAircraftTree", "AircraftTreeAllHiddenOpened", "CompletedPCBenchmark", "FocusedCampaignData", "FocusedAircraftViewer", "FocusedMusicPlayer", "FocusedCampaignRecord", "MarkedNewlyFreeMission", "MarkedNewlyFreeFlight", "MarkedNewlyDataViewer" })
        {
            var full = "ELiveMenuMiscFlagID::" + f;
            var chk = new CheckBox { Content = f, IsChecked = flags.Contains(full), Width = 220, Margin = new Thickness(6) };
            chk.Checked += (_, _) => { m.SetMenuMiscFlag(full, true); MarkDirty(f); };
            chk.Unchecked += (_, _) => { m.SetMenuMiscFlag(full, false); MarkDirty(f); };
            wrap2.Children.Add(chk);
        }
        sp.Children.Add(wrap2);

        sp.Children.Add(Label(L("Unlock entries (UnlockData)"), 15, true));
        sp.Children.Add(Muted(L("Game conditions and actions, and whether they already fired in this save. Use search to filter.")));
        var list = new StackPanel();
        void BuildList()
        {
            list.Children.Clear();
            int n = 0;
            foreach (var u in m.UnlockEntries())
            {
                data.UnlockRules.TryGetValue(u.Id, out var rule);
                var text = $"{u.Id}  {u.ActionType.Replace("ELiveCAActionType::", "")}  " + (rule != null ? string.Join(" | ", rule.Actions) + "   <=  " + string.Join(" & ", rule.Conditions) : "");
                if (search.Length > 0 && !text.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                var chk = new CheckBox { Content = text, IsChecked = u.Activated.V, Margin = new Thickness(6, 2, 6, 2) };
                var uc = u;
                chk.Checked += (_, _) => { uc.Activated.V = true; MarkDirty(uc.Id.ToString()); };
                chk.Unchecked += (_, _) => { uc.Activated.V = false; MarkDirty(uc.Id.ToString()); };
                list.Children.Add(chk);
                if (++n >= 400) { list.Children.Add(Muted(L("... use search to see the rest"))); break; }
            }
        }
        refresh = BuildList;
        BuildList();
        sp.Children.Add(list);
        PageHost.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    void PageSystem()
    {
        LblPageTitle.Text = L("Options (System.sav)");
        if (systemFile == null) { PageHost.Content = Muted(L("System.sav not loaded.")); return; }
        var sp = new StackPanel();
        foreach (var sec in systemFile.Sections)
        {
            foreach (var p in sec.Props)
            {
                if (search.Length > 0 && !p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var l = Label(p.Name); l.Width = 320; l.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(l, Dock.Left);
                row.Children.Add(l);
                row.Children.Add(ValueEditor(p.Value, p.Name));
                sp.Children.Add(row);
            }
        }
        refresh = PageSystem;
        PageHost.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static readonly Dictionary<string, string[]> Enums = LoadEnums();
    static Dictionary<string, string[]> LoadEnums()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "enums.json");
        if (!File.Exists(p)) return new();
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(p)) ?? new();
    }

    FrameworkElement ValueEditor(Value v, string label)
    {
        switch (v)
        {
            case BoolValue b:
                { var c = new CheckBox { IsChecked = b.V, VerticalAlignment = VerticalAlignment.Center }; c.Checked += (_, _) => { b.V = true; MarkDirty(label); }; c.Unchecked += (_, _) => { b.V = false; MarkDirty(label); }; return c; }
            case EnumValue ev:
                {
                    var en = ev.V.Contains("::") ? ev.V[..ev.V.IndexOf("::")] : "";
                    var cb = new ComboBox { Width = 320, IsEditable = true, HorizontalAlignment = HorizontalAlignment.Left };
                    cb.ItemsSource = Enums.TryGetValue(en, out var names) ? names.Select(n => $"{en}::{n}").ToArray() : new[] { ev.V };
                    cb.Text = ev.V;
                    cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != ev.V) { ev.V = s; MarkDirty(label); } };
                    cb.LostFocus += (_, _) => { if (cb.Text != ev.V && cb.Text.Length > 0) { ev.V = cb.Text; MarkDirty(label); } };
                    return cb;
                }
            case IntValue iv:
                { var tb = new TextBox { Text = iv.V.ToString(), Width = 160, HorizontalAlignment = HorizontalAlignment.Left }; tb.LostFocus += (_, _) => { if (long.TryParse(tb.Text, out var n) && n != iv.V) { iv.V = n; MarkDirty(label); } }; return tb; }
            case FloatValue fv:
                { var tb = new TextBox { Text = fv.V.ToString("R", CultureInfo.InvariantCulture), Width = 160, HorizontalAlignment = HorizontalAlignment.Left }; tb.LostFocus += (_, _) => { if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n != fv.V) { fv.V = n; MarkDirty(label); } }; return tb; }
            case ByteValue bv:
                { var tb = new TextBox { Text = bv.V.ToString(), Width = 160, HorizontalAlignment = HorizontalAlignment.Left }; tb.LostFocus += (_, _) => { if (byte.TryParse(tb.Text, out var n) && n != bv.V) { bv.V = n; MarkDirty(label); } }; return tb; }
            case StrValue sv:
                { var tb = new TextBox { Text = sv.V, Width = 320, HorizontalAlignment = HorizontalAlignment.Left }; tb.LostFocus += (_, _) => { if (tb.Text != sv.V) { sv.V = tb.Text; MarkDirty(label); } }; return tb; }
            case StructValue st:
                {
                    var inner = new StackPanel();
                    foreach (var p in st.Props) { var r = new DockPanel(); var l = Muted(p.Name, 11); l.Width = 160; DockPanel.SetDock(l, Dock.Left); r.Children.Add(l); r.Children.Add(ValueEditor(p.Value, label + "." + p.Name)); inner.Children.Add(r); }
                    return inner;
                }
            default: return Muted(v.ToString() ?? "", 12);
        }
    }

    // ---------- raw tree ----------

    sealed class TreeItem : INotifyPropertyChanged
    {
        public Node Node = null!;
        public string Name { get; set; } = "";
        public string Summary { get; set; } = "";
        public ObservableCollection<TreeItem> Children { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() { Summary = SummaryOf(Node); PropertyChanged?.Invoke(this, new(nameof(Summary))); }
    }

    static string SummaryOf(Node n)
    {
        Value? v = n switch { Property p => p.Value, Element e => e.Value, MapEntry m => m.Val, Value val => val, _ => null };
        if (n is MapEntry me) return $"{Leaf(me.Key)} => {Leaf(me.Val)}";
        if (v == null) return "";
        if (n is Element && v is StructValue sv) return string.Join("  ", sv.Props.Where(p => p.Value is not StructValue and not ArrayValue and not Ac8Save.SetValue and not MapValue).Take(4).Select(p => $"{p.Name}={Leaf(p.Value)}"));
        return Leaf(v);
    }

    static string Leaf(Value v) => v switch
    {
        StructValue sv => $"{{{sv.Props.Count}}}",
        ArrayValue av => $"[{av.Items.Count}]",
        Ac8Save.SetValue se => $"set[{se.Items.Count}]",
        MapValue mv => $"map[{mv.Items.Count}]",
        EnumValue ev => ev.V.Contains("::") ? ev.V[(ev.V.IndexOf("::") + 2)..] : ev.V,
        RawValue rv => $"{rv.Data.Length} bytes",
        _ => v.ToString() ?? "",
    };

    TreeItem BuildTree(Node n)
    {
        var ti = new TreeItem { Node = n, Name = n switch { Property p => p.Name, Element e => $"[{e.Index}]", MapEntry m => $"[{m.Index}]", _ => n.Label }, Summary = SummaryOf(n) };
        foreach (var c in n.Children) ti.Children.Add(BuildTree(c));
        return ti;
    }

    void PageRaw()
    {
        LblPageTitle.Text = L("Advanced: full tree");
        var roots = new ObservableCollection<TreeItem>();
        foreach (var (name, sf) in new (string, SaveFile?)[] { ("Campaign.sav", campaignFile), ("System.sav", systemFile) })
        {
            if (sf == null) continue;
            var fileItem = new TreeItem { Name = name, Node = sf.Sections[0], Summary = sf.ClassName };
            for (int i = 0; i < sf.Sections.Count; i++)
            {
                var secItem = new TreeItem { Name = $"{L("Block")} {i}", Node = sf.Sections[i], Summary = $"{sf.Sections[i].Props.Count} {L("properties")}" };
                foreach (var p in sf.Sections[i].Props) secItem.Children.Add(BuildTree(p));
                fileItem.Children.Add(secItem);
            }
            roots.Add(fileItem);
        }
        var tree = new TreeView { ItemsSource = roots };
        var tmpl = new HierarchicalDataTemplate { ItemsSource = new System.Windows.Data.Binding("Children") };
        var sp = new FrameworkElementFactory(typeof(StackPanel)); sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var t1 = new FrameworkElementFactory(typeof(TextBlock)); t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name")); t1.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var t2 = new FrameworkElementFactory(typeof(TextBlock)); t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Summary")); t2.SetValue(TextBlock.ForegroundProperty, Res("Fg2")); t2.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 0, 0));
        sp.AppendChild(t1); sp.AppendChild(t2);
        tmpl.VisualTree = sp;
        tree.ItemTemplate = tmpl;
        tree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is not TreeItem ti) return;
            Detail.Children.Clear();
            Detail.Children.Add(Label(ti.Name, 15, true));
            var v = ti.Node switch { Property p => p.Value, Element el => el.Value, MapEntry me => me.Val, Value val => val, _ => null };
            if (ti.Node is Property pp) Detail.Children.Add(Muted(pp.Type.ToString(), 11));
            if (v != null && v is not StructValue and not ArrayValue and not Ac8Save.SetValue and not MapValue)
            {
                var ed = ValueEditor(v, ti.Name);
                Detail.Children.Add(ed);
                var apply = new Button { Content = L("Apply"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                apply.Click += (_, _) => { apply.Focus(); ti.Refresh(); };
                Detail.Children.Add(apply);
            }
            else Detail.Children.Add(Muted(SummaryOf(ti.Node)));
        };
        PageHost.Content = tree;
    }
}