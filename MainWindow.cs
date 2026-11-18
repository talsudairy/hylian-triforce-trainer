using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Tozot;

public sealed class MainWindow : Window
{
    static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hylian Triforce", "settings.txt");
    static readonly string OldSettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TOZOT", "settings.txt");
    static readonly string[] Wallets = { "Small", "Medium", "Large" };
    internal static readonly string[] BottleLetters = { "A", "B", "C", "D", "E" };

    string gameDir;
    GameMemory? live;
    readonly TextBox log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 76, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Theme.Dim, Margin = new Thickness(0) };
    readonly TextBlock gameDirText = Theme.Label("");

    // live controls
    readonly TextBlock liveStatus = Theme.Label("Not attached", dim: true);
    readonly StackPanel liveState = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBox liveRupees = Theme.Box("0");
    readonly ComboBox liveWallet = Theme.Combo(Wallets, 130);
    readonly ComboBox liveCategory = Theme.Combo(Array.Empty<string>(), 240);
    readonly ListBox liveItems = ItemList();
    readonly ComboBox liveBottle = Theme.Combo(BottleLetters.Select(b => "Bottle " + b), 120);
    readonly ComboBox liveContents = Theme.Combo(Fields.BottleContents.Select(Spaced), 170);
    readonly Button undo = Theme.Button("Undo last change");
    // switches: infinite items and rupees (0.3 s refill), hearts and magic (0.1 s); ticked ones are remembered
    static readonly string SwitchesFile = Path.Combine(Path.GetDirectoryName(SettingsFile)!, "switches.txt");
    readonly Dictionary<CheckBox, Ammo.Kind> itemBoxes = Ammo.All.ToDictionary(k => Switch("item:" + k.Name, "Infinite " + k.Name), k => k);
    readonly CheckBox infRupees = Switch("rupees", "Infinite rupees (kept at your wallet's size)");
    readonly DispatcherTimer ammoTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Meter health = new(Kept.Health(), Switch("health", "Infinite health"), "Link's hearts");
    readonly Meter magic = new(Kept.Magic(), Switch("magic", "Infinite magic"), "Link's magic");
    readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Kept containers = Kept.HeartContainers();
    readonly TextBox liveHearts = Theme.Box("3", 60);
    bool restoring;   // re-ticking remembered switches: don't rewrite the file meanwhile

    static CheckBox Switch(string key, string text) => new() { Content = text, Tag = key, Margin = new Thickness(0, 4, 28, 4) };

    /// <summary>A meter switch (hearts or magic): the handle search runs off the UI thread and is redone after a load.</summary>
    sealed class Meter
    {
        public readonly Kept Kept; public readonly CheckBox Box; public readonly string What;
        public bool Searching; public DateTime LastSearch; public bool RecordOnly;
        public Meter(Kept k, CheckBox b, string what) { Kept = k; Box = b; What = what; }
    }

    // save controls
    readonly ComboBox saveSlot = Theme.Combo(Array.Empty<string>(), 420);
    readonly StackPanel saveState = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBox saveRupees = Theme.Box("0");
    readonly TextBox saveHearts = Theme.Box("3", 60);
    readonly ComboBox saveWallet = Theme.Combo(Wallets, 130);
    readonly ComboBox saveCategory = Theme.Combo(Array.Empty<string>(), 240);
    readonly ListBox saveItems = ItemList();
    readonly ComboBox saveBottle = Theme.Combo(BottleLetters.Select(b => "Bottle " + b), 120);
    readonly ComboBox saveContents = Theme.Combo(Fields.BottleContents.Select(Spaced), 170);
    string[] slotDirs = Array.Empty<string>();

    public MainWindow()
    {
        Title = Setup.AppName;
        Width = 900; Height = 860; MinWidth = 760; MinHeight = 620;
        Background = Theme.Bg; Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 14;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/HylianTriforce.ico"));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => { Theme.DarkTitleBar(this); RegisterHotkeys(); };
        gameDir = LoadGameDir();

        // header: icon, title, game folder
        var browse = Theme.Button("Change");
        browse.Click += (_, _) => PickGameDir();
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Hylian Triforce", FontFamily = Theme.Serif, FontSize = 28, Foreground = Theme.Gold });
        title.Children.Add(new TextBlock { Text = "Trainer and save editor for Ocarina of Time", Foreground = Theme.Dim });
        var head = new DockPanel { Margin = new Thickness(24, 18, 24, 10) };
        var icon = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/HylianTriforceShield.png")), Width = 68, Height = 68, Margin = new Thickness(0, 0, 14, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(icon, Dock.Left);
        var folder = Theme.Row(Theme.Label("Game folder", dim: true), gameDirText, browse);
        folder.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(folder, Dock.Right);
        head.Children.Add(icon); head.Children.Add(folder); head.Children.Add(title);

        tabs = new TabControl { Margin = new Thickness(24, 4, 24, 0) };
        tabs.Items.Add(new TabItem { Header = "Live, while playing", Content = Scroll(LiveTab()) });
        tabs.Items.Add(new TabItem { Header = "Save editor, game closed", Content = Scroll(SaveTab()) });

        var activity = Theme.Card("", Theme.Label("Activity", dim: true), log);
        activity.Margin = new Thickness(24, 6, 24, 18);
        activity.Padding = new Thickness(16, 10, 16, 12);

        var layout = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(activity, Dock.Bottom);
        layout.Children.Add(head); layout.Children.Add(activity); layout.Children.Add(tabs);
        var root = new Grid();
        root.Children.Add(Theme.Backdrop());
        root.Children.Add(layout);
        Content = root;

        foreach (var c in Categories()) { liveCategory.Items.Add(c); saveCategory.Items.Add(c); }
        liveCategory.SelectedIndex = saveCategory.SelectedIndex = 0;
        liveCategory.SelectionChanged += (_, _) => FillItems(liveItems, liveCategory, id => live == null ? null : live.Get(Fields.Get("IsGet." + id)) != 0);
        saveCategory.SelectionChanged += (_, _) => FillSaveItems();
        saveSlot.SelectionChanged += (_, _) => { ShowSlot(); FillSaveItems(); };
        FillItems(liveItems, liveCategory, _ => null);   // the list before attaching: no check marks yet
        ShowGameDir();
        Closed += (_, _) => live?.Dispose();
        Log("Ready. Live changes need the game running with a save loaded; the save editor needs the game closed.");
    }

    TabControl tabs;

    /// <summary>
    /// Pictures for the README (hidden "--shots &lt;dir&gt; [game folder]" mode): attaches if the game is running, then
    /// renders each tab at its full height to PNG at 150 % scale. Changes nothing in the game or the saves.
    /// </summary>
    public async Task TakeShots(string dir, string? folder)
    {
        Directory.CreateDirectory(dir);
        if (folder != null) { gameDir = folder; ShowGameDir(); }
        log.Clear();
        if (GameMemory.FindGame() != null)
        {
            live = GameMemory.Attach();
            liveStatus.Text = $"Attached to {live.Runner.Name} (process {live.ProcessId}), checks passed"; liveStatus.Foreground = Theme.Good;
            RefreshLive();
            Log("Attached. The game's data tables match the expected layout.");
            foreach (var b in new[] { health.Box, itemBoxes.Keys.First() }) { restoring = true; b.IsChecked = true; restoring = false; }
            await Task.Delay(4000);   // the hearts search, then the refill
            RefreshLive();
        }
        else Log("Ready. Live changes need the game running with a save loaded; the save editor needs the game closed.");
        await Task.Delay(500);
        Save((FrameworkElement)Content, Path.Combine(dir, "window.png"), null);   // the window as it opens
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            await Task.Delay(800);
            UpdateLayout();
            // the tab's whole content (a scroll viewer gives it its full height), drawn on the window's own background
            var content = (FrameworkElement)((ScrollViewer)((TabItem)tabs.Items[i]).Content).Content;
            Save(content, Path.Combine(dir, i == 0 ? "live.png" : "save-editor.png"), Theme.Bg);
        }
        foreach (var b in AllSwitches) { restoring = true; b.IsChecked = false; restoring = false; }
        Close();
    }

    static void Save(FrameworkElement e, string path, Brush? background)
    {
        var bounds = VisualTreeHelper.GetDescendantBounds(e);   // cards may draw past the panel's own width
        double w = Math.Max(e.ActualWidth, bounds.Right), h = Math.Max(e.ActualHeight, bounds.Bottom), pad = background == null ? 0 : 24;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background != null) dc.DrawRectangle(background, null, new Rect(0, 0, w + 2 * pad, h + 2 * pad));
            dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h) }, null, new Rect(pad, pad, w, h));
        }
        var bmp = new RenderTargetBitmap((int)((w + 2 * pad) * 1.5), (int)((h + 2 * pad) * 1.5), 144, 144, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }

    static ScrollViewer Scroll(UIElement e) => new() { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) };

    static ListBox ItemList() => new() { Height = 200, Width = 460, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 4, 12, 4) };

    static StackPanel Column(params UIElement[] items)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var i in items) { if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 0, 8); p.Children.Add(i); }
        return p;
    }

    // ---------------------------------------------------------------- live tab

    UIElement LiveTab()
    {
        var p = new StackPanel();
        var attach = Theme.Button("Attach to the game", accent: true);
        attach.Click += (_, _) => Attach();
        var refresh = Theme.Button("Refresh");
        refresh.Click += (_, _) => RefreshLive();
        p.Children.Add(Theme.Card("The game", Theme.Row(attach, refresh, liveStatus), liveState));

        var setR = Theme.Button("Set rupees", accent: true);
        setR.Click += (_, _) => LiveDo("Set rupees", () => new[] { (Fields.Get("PlayerStatus.CurrentRupee"), ParseRupees(liveRupees.Text)) });
        var upW = Theme.Button("Upgrade wallet", accent: true);
        upW.Click += (_, _) => LiveDo("Upgrade wallet", () => WalletChanges(liveWallet.SelectedIndex));
        p.Children.Add(TwoCards(
            Theme.Card("Rupees", Theme.Row(liveRupees, setR), Theme.Row(infRupees), Theme.Label("The game may hold rupees to your wallet's size.", dim: true)),
            Theme.Card("Wallet", Theme.Row(liveWallet, upW))));

        var add = Theme.Button("Add item", accent: true);
        add.Click += (_, _) => LiveDo("Add item", () => Selected(liveItems).Select(id => (Fields.Get("IsGet." + id), 1u)));
        var remove = Theme.Button("Remove item");
        remove.Click += (_, _) => LiveDo("Remove item", () => Selected(liveItems).Select(id => (Fields.Get("IsGet." + id), 0u)));
        p.Children.Add(Theme.Card("Items, weapons, songs and upgrades",
            Theme.Row(liveCategory),
            Theme.Row(liveItems, Column(add, remove)),
            Theme.Label("Select one or more (Ctrl or Shift), then Add. A check mark shows what you have.", dim: true)));

        var setB = Theme.Button("Set bottle", accent: true);
        setB.Click += (_, _) => LiveDo("Set bottle", () => BottleChanges(liveBottle.SelectedIndex, liveContents.SelectedIndex));
        var removeB = Theme.Button("Remove bottle");
        removeB.Click += (_, _) => LiveDo("Remove bottle", () => BottleRemoval(liveBottle.SelectedIndex, f => live!.Get(f)));
        p.Children.Add(Theme.Card("Bottles and potions", Theme.Row(liveBottle, liveContents, setB, removeB),
            Theme.Label("Remove bottle takes the chosen bottle away, not just empties it.", dim: true)));

        foreach (var (box, kind) in itemBoxes)
        {
            box.Checked += (_, _) => AmmoSwitch(box, kind, true);
            box.Unchecked += (_, _) => AmmoSwitch(box, kind, false);
        }
        infRupees.Checked += (_, _) => RupeeSwitch(true);
        infRupees.Unchecked += (_, _) => RupeeSwitch(false);
        ammoTimer.Tick += (_, _) => AmmoTick();
        foreach (var m in new[] { health, magic })
        {
            m.Box.Checked += (_, _) => MeterSwitch(m, true);
            m.Box.Unchecked += (_, _) => MeterSwitch(m, false);
        }
        meterTimer.Tick += (_, _) => MeterTick();
        var items = new WrapPanel();
        foreach (var b in itemBoxes.Keys) { b.MinWidth = 230; items.Children.Add(b); }
        p.Children.Add(Theme.Card("Infinite items",
            items,
            Theme.Label("Ticked, the count fills back up to the most your bag, quiver or pouch holds as you use it. Untick to turn it off; your count then works as normal.", dim: true)));

        undo.Click += (_, _) => Guard(() =>
        {
            if (live == null) throw new InvalidOperationException("Not attached.");
            Log(live.Undo());
            RefreshLive();
        });
        undo.IsEnabled = false;
        var setH = Theme.Button("Set heart containers", accent: true);
        setH.Click += (_, _) => SetContainers();
        p.Children.Add(TwoCards(
            Theme.Card("Hearts and magic", Theme.Row(health.Box, magic.Box),
                Theme.Row(liveHearts, setH),
                Theme.Label("Ticked, hearts or magic fill back up right after you lose some. Heart containers: 1 to 20, and your hearts are filled to match.", dim: true)),
            Theme.Card("Undo", Theme.Row(undo), Theme.Label("Puts back what the last live change replaced.", dim: true))));
        p.Children.Add(Theme.Card("Hotkeys and remembered switches",
            Theme.Label("F2 hides or shows this window, F3 fills your hearts, F4 fills your wallet. They work anywhere, also while you play; F3 and F4 need the trainer attached.", dim: true),
            Theme.Label("The switches you tick are remembered and turned back on the next time you attach.", dim: true)));
        return p;
    }

    // ---------------------------------------------------------------- hotkeys (F2 hide/show, F3 full hearts, F4 full wallet)

    [System.Runtime.InteropServices.DllImport("user32", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    void RegisterHotkeys()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var failed = new List<string>();
        foreach (var (id, vk, name) in new[] { (1, 0x71u, "F2"), (2, 0x72u, "F3"), (3, 0x73u, "F4") })
            if (!RegisterHotKey(hwnd, id, MOD_NOREPEAT, vk)) failed.Add(name);
        if (failed.Count > 0) Log($"Hotkey {string.Join(", ", failed)} is in use by another program, so it won't work here.");
        System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            handled = true;
            switch ((int)wp)
            {
                case 1:
                    if (IsVisible && WindowState != WindowState.Minimized) Hide();
                    else { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
                    break;
                case 2: FullHeal(); break;
                case 3: FillWallet(); break;
            }
            return IntPtr.Zero;
        });
    }

    // ---------------------------------------------------------------- switches

    IEnumerable<CheckBox> AllSwitches => itemBoxes.Keys.Append(infRupees).Append(health.Box).Append(magic.Box);

    void SaveSwitches()
    {
        if (restoring) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SwitchesFile)!);
            File.WriteAllLines(SwitchesFile, AllSwitches.Where(b => b.IsChecked == true).Select(b => (string)b.Tag));
        }
        catch { }
    }

    /// <summary>After attaching: ticks again what was on last time (each switch then checks the game as usual).</summary>
    void RestoreSwitches()
    {
        string[] on;
        try { on = File.Exists(SwitchesFile) ? File.ReadAllLines(SwitchesFile) : Array.Empty<string>(); } catch { return; }
        var boxes = AllSwitches.Where(b => on.Contains((string)b.Tag) && b.IsChecked != true).ToList();
        if (boxes.Count == 0) return;
        Log("Turning back on what you had on last time: " + string.Join(", ", boxes.Select(b => b.Content)) + ".");
        restoring = true;
        try { foreach (var b in boxes) b.IsChecked = true; }
        finally { restoring = false; }
    }

    /// <summary>The game closed or went back to the title: every switch goes off (remembered ones come back on attach).</summary>
    void AllOff(string why)
    {
        ammoTimer.Stop(); meterTimer.Stop();
        if (!AllSwitches.Any(b => b.IsChecked == true)) return;
        Log(why);
        restoring = true;   // keep the remembered list as it was
        try { foreach (var b in AllSwitches) b.IsChecked = false; }
        finally { restoring = false; }
    }

    static readonly uint[] WalletSizes = { 99, 200, 500 };   // none or Small, Medium, Large (the game's own wallets)

    uint WalletSize(GameMemory g) => WalletSizes[WalletOf(id => g.Get(Fields.Get("IsGet." + id)) != 0)];

    void RupeeSwitch(bool on)
    {
        SaveSwitches();
        if (!on) { Log("Infinite rupees: off."); return; }
        if (live == null || !live.Alive) { Refuse(infRupees, "infinite rupees"); return; }
        try { Log($"Infinite rupees: on, kept at {WalletSize(live)}."); }
        catch (Exception ex) { infRupees.IsChecked = false; Log("Not done: " + ex.Message); return; }
        ammoTimer.Start();
        AmmoTick();
    }

    void Refuse(CheckBox box, string what)
    {
        if (restoring) return;   // not attached yet while restoring can't happen, but stay quiet if it does
        box.IsChecked = false;
        Log($"Not done: attach to the game first, then turn on {what}.");
    }

    void SetContainers() => Guard(() =>
    {
        if (live == null || !live.Alive) throw new InvalidOperationException("Attach to the game first (the game must be running with a save loaded).");
        if (!uint.TryParse(liveHearts.Text.Trim(), out uint n) || n < 1 || n > 20) throw new ArgumentException("Heart containers: a number from 1 to 20.");
        if (settingContainers) { Log("Heart containers: still finding the game's counters, one moment."); return; }
        uint q = n * 4;
        var g = live;
        bool findContainers = containers.Found == 0 || !containers.Valid(g), findHearts = health.Kept.Found == 0 || !health.Kept.Valid(g);
        if (!findContainers && !findHearts) { FinishContainers(g, n, q); return; }
        // the search takes seconds: off the UI thread, so the window doesn't freeze
        settingContainers = true;
        Log("Heart containers: finding the game's counters (a few seconds)...");
        Task.Run(() => { if (findContainers) containers.Find(g); if (findHearts) health.Kept.Find(g); }).ContinueWith(t =>
        {
            settingContainers = false;
            if (live != g || !g.Alive) return;
            if (t.Exception != null) { Log("Not done: " + (t.Exception.InnerException ?? t.Exception).Message); return; }
            Guard(() => FinishContainers(g, n, q));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    });

    bool settingContainers;

    void FinishContainers(GameMemory g, uint n, uint q)
    {
        g.Apply("Set heart containers", new[] { (Fields.Get("PlayerStatus.MaxLife"), q), (Fields.Get("PlayerStatus.Life"), q) });
        bool held = containers.Set(g, q) != null;     // the game's own counters, so the change sticks
        health.Kept.Set(g, q);
        g.ForgetUndo();   // the game copies its counters over the records every frame, so Undo could not put this back
        Log(held
            ? $"Heart containers: {n}, hearts filled. Undo can't take this back: to go back, set the old number."
            : "Heart containers not kept: the game's own counter was not found, so the game puts the old number back. Load your save and be in the game (not the title screen), then try again.");
        RefreshLive();
    }

    void FullHeal()
    {
        if (live == null || !live.Alive) return;
        var g = live;
        try
        {
            if (health.Kept.Found > 0 && health.Kept.Valid(g)) { FillHearts(g); return; }
        }
        catch (Exception ex) { Log("F3 not done: " + ex.Message); return; }
        if (healSearching) return;   // F3 pressed again while the search runs
        // after an area load the old handle is gone: search off the UI thread so the game and window don't stall
        healSearching = true;
        Task.Run(() => health.Kept.Find(g)).ContinueWith(t =>
        {
            healSearching = false;
            if (live != g || !g.Alive) return;
            if (t.Exception != null) { Log("F3 not done: " + (t.Exception.InnerException ?? t.Exception).Message); return; }
            try { FillHearts(g); } catch (Exception ex) { Log("F3 not done: " + ex.Message); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    bool healSearching;

    void FillHearts(GameMemory g)
    {
        uint max = health.Kept.Max(g);
        if (health.Kept.Set(g, max) == null) g.Keep(Fields.Get("PlayerStatus.Life"), max);
        Log($"F3: hearts filled ({max / 4.0:0.##}).");
    }

    void FillWallet()
    {
        if (live == null || !live.Alive) return;
        try
        {
            uint size = WalletSize(live);
            live.Keep(Fields.Get("PlayerStatus.CurrentRupee"), size);
            Log($"F4: wallet filled ({size} rupees).");
        }
        catch (Exception ex) { Log("F4 not done: " + ex.Message); }
    }

    void AmmoSwitch(CheckBox box, Ammo.Kind kind, bool on)
    {
        SaveSwitches();
        if (!on) { Log($"Infinite {kind.Name}: off."); return; }
        if (live == null || !live.Alive) { Refuse(box, "infinite " + kind.Name); return; }
        try
        {
            uint full = Ammo.Full(live, kind);
            Log(full == 0
                ? $"Infinite {kind.Name}: on; it starts filling once you can carry some."
                : $"Infinite {kind.Name}: on, kept at {full}.");
        }
        catch (Exception ex) { box.IsChecked = false; Log("Not done: " + ex.Message); return; }
        ammoTimer.Start();
        AmmoTick();
    }

    void AmmoTick()
    {
        if (live == null || !live.Alive) { AllOff("The game has closed: the switches are off (they come back on when you attach again)."); return; }
        if (!itemBoxes.Keys.Append(infRupees).Any(b => b.IsChecked == true)) { ammoTimer.Stop(); return; }
        try
        {
            foreach (var (box, kind) in itemBoxes)
                if (box.IsChecked == true) Ammo.TopUp(live, kind);
            if (infRupees.IsChecked == true)
            {
                var f = Fields.Get("PlayerStatus.CurrentRupee");
                uint size = WalletSize(live);
                if (live.Get(f) < size) live.Keep(f, size);
            }
        }
        catch (Exception ex)
        {
            // e.g. back on the title screen: stop quietly instead of a message box every 0.3 s
            AllOff("The switches turned off: " + ex.Message);
        }
    }

    void MeterSwitch(Meter m, bool on)
    {
        SaveSwitches();
        if (!on) { Log($"{m.Box.Content}: off."); return; }
        if (live == null || !live.Alive) { Refuse(m.Box, ((string)m.Box.Content).ToLowerInvariant()); return; }
        Log($"{m.Box.Content}: finding {m.What} in the game (a few seconds)...");
        MeterSearch(m, first: true);
    }

    /// <summary>Searches off the UI thread, then starts the refill; on the title screen there is nothing to find yet.</summary>
    void MeterSearch(Meter m, bool first)
    {
        if (m.Searching || live == null || (!first && DateTime.Now - m.LastSearch < TimeSpan.FromSeconds(5))) return;
        m.Searching = true; m.LastSearch = DateTime.Now;
        var g = live;
        Task.Run(() => { try { return m.Kept.Find(g); } catch { return 0; } }).ContinueWith(t =>
        {
            m.Searching = false;
            if (m.Box.IsChecked != true || live != g) return;
            uint max;
            try { max = m.Kept.Max(g); }
            catch (Exception ex)
            {
                // on a re-search, don't let this turn off the remembered switch (restoring keeps SaveSwitches out)
                if (!first) restoring = true;
                m.Box.IsChecked = false;
                restoring = false;
                Log("Not done: " + ex.Message); return;
            }
            if (first && max == 0) { m.Box.IsChecked = false; Log($"Not done: {m.What} have no maximum yet in this save (no magic meter yet?)."); return; }
            m.RecordOnly = t.Result == 0;
            if (m.RecordOnly && m == health)
            {
                if (first) { m.Box.IsChecked = false; Log("Not done: Link's hearts were not found. Load your save and be in the game (not the title screen), then tick it again."); }
                return;   // a later search after a load will try again
            }
            if (first) Log($"{m.Box.Content}: on, kept at {(m == health ? $"{max / 4.0:0.##} hearts" : max.ToString())}"
                + (m.RecordOnly ? " (written straight to the save data: the game's own counter was not found)." : "."));
            meterTimer.Start();
            MeterTick();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void MeterTick()
    {
        if (live == null || !live.Alive) { AllOff("The game has closed: the switches are off (they come back on when you attach again)."); return; }
        if (health.Box.IsChecked != true && magic.Box.IsChecked != true) { meterTimer.Stop(); return; }
        try
        {
            foreach (var m in new[] { health, magic })
            {
                if (m.Box.IsChecked != true) continue;
                if (m.RecordOnly)
                {
                    var f = Fields.Get(m.Kept.ValueName); uint max = m.Kept.Max(live);
                    if (max > 0 && live.Get(f) < max) live.Keep(f, max);
                    if (!m.Searching) MeterSearch(m, first: false);   // the game may make its counter later
                }
                else if (m.Kept.Valid(live)) m.Kept.TopUp(live);
                else MeterSearch(m, first: false);   // the game made a new one (a save or area load): find it again
            }
        }
        catch (Exception ex) { AllOff("The switches turned off: " + ex.Message); }
    }

    static Grid TwoCards(Border a, Border b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(b, 2);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    void Attach() => Guard(() =>
    {
        live?.Dispose();
        live = null;
        live = GameMemory.Attach();
        liveStatus.Text = $"Attached to {live.Runner.Name} (process {live.ProcessId}), checks passed";
        liveStatus.Foreground = Theme.Good;
        Log("Attached. The game's data tables match the expected layout (Bool 11292, Int 1259, Enum 130 entries).");
        RefreshLive();
        RestoreSwitches();
    }, () => { liveStatus.Text = "Not attached"; liveStatus.Foreground = Theme.Bad; });

    void RefreshLive() => Guard(() =>
    {
        if (live == null) return;
        if (!live.Alive) { live.Dispose(); live = null; liveStatus.Text = "The game has closed"; liveStatus.Foreground = Theme.Dim; liveState.Children.Clear(); return; }
        uint r = live.Get(Fields.Get("PlayerStatus.CurrentRupee"));
        uint life = live.Get(Fields.Get("PlayerStatus.Life")), max = live.Get(Fields.Get("PlayerStatus.MaxLife"));
        int wallet = WalletOf(id => live.Get(Fields.Get("IsGet." + id)) != 0);
        liveRupees.Text = r.ToString();
        liveWallet.SelectedIndex = wallet;
        liveHearts.Text = (max / 4).ToString();
        ShowState(liveState, life, max, r, wallet);
        FillItems(liveItems, liveCategory, id => live.Get(Fields.Get("IsGet." + id)) != 0);
        undo.IsEnabled = live.LastAction.Count > 0;
    });

    static void ShowState(StackPanel panel, uint life, uint max, uint rupees, int wallet)
    {
        panel.Children.Clear();
        panel.Children.Add(Theme.Hearts(life, max));
        panel.Children.Add(new Border { Width = 24 });
        panel.Children.Add(Theme.Rupees(rupees));
        panel.Children.Add(new Border { Width = 24 });
        panel.Children.Add(Theme.Label($"{Wallets[wallet]} wallet  ·  {Hearts(life)} of {Hearts(max)} hearts", dim: true));
    }

    void LiveDo(string name, Func<IEnumerable<(Field, uint)>> changes) => Guard(() =>
    {
        if (live == null || !live.Alive) throw new InvalidOperationException("Attach to the game first (the game must be running with a save loaded).");
        var list = changes().ToList();
        if (list.Count == 0) throw new InvalidOperationException("Nothing selected.");
        live.Apply(name, list);
        Log($"{name}: {string.Join(", ", list.Select(c => $"{Short(c.Item1)} = {Show(c.Item1, c.Item2)}"))}.");
        RefreshLive();
    });

    // ---------------------------------------------------------------- save tab

    UIElement SaveTab()
    {
        var p = new StackPanel();
        var reload = Theme.Button("Reload");
        reload.Click += (_, _) => LoadSlots();
        var backups = Theme.Button("Open backups");
        backups.Click += (_, _) => Guard(() =>
        {
            var d = Path.Combine(SaveFile.DataDir(gameDir) ?? throw new InvalidOperationException("No saves in this game folder yet."), "TOZOT-backups");
            Directory.CreateDirectory(d);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{d}\"") { UseShellExecute = true });
        });
        p.Children.Add(Theme.Card("Save slot", Theme.Row(saveSlot, reload, backups), saveState,
            Theme.Label("Every change backs up the whole slot first, into a TOZOT-backups folder next to the game's save folder.", dim: true)));

        var heal = Theme.Button("Heal", accent: true);
        heal.Click += (_, _) => SaveDo("Heal", s => new[] { (Fields.Get("PlayerStatus.Life"), s.Get(Fields.Get("PlayerStatus.MaxLife"))) });
        var setH = Theme.Button("Set heart containers", accent: true);
        setH.Click += (_, _) => SaveDo("Set heart containers", s =>
        {
            if (!int.TryParse(saveHearts.Text, out int n) || n < 3 || n > 20) throw new ArgumentException("Heart containers: a number from 3 to 20.");
            uint max = (uint)(n * 4);   // the game counts quarter hearts
            uint life = Math.Min(s.Get(Fields.Get("PlayerStatus.Life")), max);
            return new[] { (Fields.Get("PlayerStatus.MaxLife"), max), (Fields.Get("PlayerStatus.Life"), life) };
        });
        p.Children.Add(Theme.Card("Hearts", Theme.Row(heal, new Border { Width = 18 }, saveHearts, setH, Theme.Label("3 to 20", dim: true))));

        var setR = Theme.Button("Set rupees", accent: true);
        setR.Click += (_, _) => SaveDo("Set rupees", _ => new[] { (Fields.Get("PlayerStatus.CurrentRupee"), ParseRupees(saveRupees.Text)) });
        var upW = Theme.Button("Upgrade wallet", accent: true);
        upW.Click += (_, _) => SaveDo("Upgrade wallet", _ => WalletChanges(saveWallet.SelectedIndex));
        p.Children.Add(TwoCards(Theme.Card("Rupees", Theme.Row(saveRupees, setR)), Theme.Card("Wallet", Theme.Row(saveWallet, upW))));

        var add = Theme.Button("Add item", accent: true);
        add.Click += (_, _) => SaveDo("Add item", _ => Selected(saveItems).Select(id => (Fields.Get("IsGet." + id), 1u)));
        var remove = Theme.Button("Remove item");
        remove.Click += (_, _) => SaveDo("Remove item", _ => Selected(saveItems).Select(id => (Fields.Get("IsGet." + id), 0u)));
        p.Children.Add(Theme.Card("Items, weapons, songs and upgrades", Theme.Row(saveCategory), Theme.Row(saveItems, Column(add, remove))));

        var setB = Theme.Button("Set bottle", accent: true);
        setB.Click += (_, _) => SaveDo("Set bottle", _ => BottleChanges(saveBottle.SelectedIndex, saveContents.SelectedIndex));
        var removeB = Theme.Button("Remove bottle");
        removeB.Click += (_, _) => SaveDo("Remove bottle", s => BottleRemoval(saveBottle.SelectedIndex, s.Get));
        p.Children.Add(Theme.Card("Bottles and potions", Theme.Row(saveBottle, saveContents, setB, removeB),
            Theme.Label("Remove bottle takes the chosen bottle away, not just empties it.", dim: true)));

        var export = Theme.Button("Export for console", accent: true);
        export.Click += (_, _) => ExportForConsole();
        p.Children.Add(Theme.Card("Export for Switch 2", Theme.Row(export),
            Theme.Label("Packages a copy of your whole save data (all slots) as a folder and a zip for a console save manager. Your PC saves are not changed. Putting it on a Switch 2 needs a homebrew save manager on the console; Nintendo offers no way to copy a save from a PC, and this export has not been tried on a console.", dim: true)));
        return p;
    }

    void LoadSlots() => Guard(() =>
    {
        saveSlot.Items.Clear();
        var (_, slots, problem) = SaveFile.FindSlots(gameDir);
        slotDirs = slots;
        if (problem != null) { saveState.Children.Clear(); saveState.Children.Add(Theme.Label(problem, dim: true)); return; }
        foreach (var d in slots)
        {
            var slot = Path.GetFileName(d).Replace("slot_", "Slot ");
            try   // one unreadable slot must not hide the slots after it
            {
                var s = new SaveFile(d);
                saveSlot.Items.Add($"{slot}  ·  {Hearts(s.Get(Fields.Get("PlayerStatus.MaxLife")))} hearts, {s.Get(Fields.Get("PlayerStatus.CurrentRupee"))} rupees  ·  saved {File.GetLastWriteTime(s.Path):g}");
            }
            catch (Exception ex)
            {
                saveSlot.Items.Add($"{slot}  ·  can't be read ({ex.Message})");
                Log($"{slot} can't be read: {ex.Message}");
            }
        }
        saveSlot.SelectedIndex = 0;
    });

    SaveFile? CurrentSlot() => saveSlot.SelectedIndex >= 0 && saveSlot.SelectedIndex < slotDirs.Length ? new SaveFile(slotDirs[saveSlot.SelectedIndex]) : null;

    void ShowSlot() => Guard(() =>
    {
        var s = CurrentSlot();
        if (s == null) return;
        uint life = s.Get(Fields.Get("PlayerStatus.Life")), max = s.Get(Fields.Get("PlayerStatus.MaxLife")), r = s.Get(Fields.Get("PlayerStatus.CurrentRupee"));
        int wallet = WalletOf(id => s.Get(Fields.Get("IsGet." + id)) != 0);
        saveHearts.Text = Hearts(max);
        saveRupees.Text = r.ToString();
        saveWallet.SelectedIndex = wallet;
        ShowState(saveState, life, max, r, wallet);
    });

    void FillSaveItems() => Guard(() =>
    {
        var s = CurrentSlot();
        FillItems(saveItems, saveCategory, id => s == null ? null : s.Get(Fields.Get("IsGet." + id)) != 0);
    });

    void SaveDo(string name, Func<SaveFile, IEnumerable<(Field, uint)>> changes) => Guard(() =>
    {
        if (GameMemory.FindGame() != null)
            throw new InvalidOperationException("The game is running. Quit the game first: it would write its own save over these changes. (For changes while playing, use the Live tab.)");
        var s = CurrentSlot() ?? throw new InvalidOperationException("Choose a save slot first.");
        var list = changes(s).ToList();
        if (list.Count == 0) throw new InvalidOperationException("Nothing selected.");
        foreach (var (f, _) in list)
            if (!s.Has(f)) throw new InvalidOperationException($"{f.Name} is not in this save: nothing was changed.");
        var backup = s.Backup();
        foreach (var (f, v) in list) s.Set(f, v);
        s.Write();
        Log($"{name} in {Path.GetFileName(s.SlotDir)}: {string.Join(", ", list.Select(c => $"{Short(c.Item1)} = {Show(c.Item1, c.Item2)}"))}. Backup: {backup}");
        int keep = saveSlot.SelectedIndex;
        LoadSlots();
        saveSlot.SelectedIndex = keep;
    });

    /// <summary>A copy of the game's whole save data (the folder the game mounts as its save: account, options and every
    /// slot), as a folder and a zip with a note on importing. Nothing in the game's folder changes.</summary>
    void ExportForConsole() => Guard(() =>
    {
        if (GameMemory.FindGame() != null) throw new InvalidOperationException("Quit the game first, so the export holds a finished save.");
        var pick = new OpenFolderDialog { Title = "Where to put the export", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (pick.ShowDialog(this) != true) return;
        var (dest, zip, count) = ExportSaveData(gameDir, pick.FolderName);
        Log($"Exported your save data ({count} slots) to {dest} and {zip}.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
    });

    public static (string dest, string zip, int slots) ExportSaveData(string gameDir, string parent)
    {
        var (account, slots, problem) = SaveFile.FindSlots(gameDir);
        if (problem != null || account == null) throw new InvalidOperationException(problem ?? "No save data found.");
        var name = $"Ocarina of Time save {DateTime.Now:yyyy-MM-dd HHmm}";
        var dest = Path.Combine(parent, name);
        if (Directory.Exists(dest)) throw new IOException($"{dest} already exists.");
        var saveDir = Path.Combine(dest, "save");
        CopyTree(account, saveDir);
        File.WriteAllText(Path.Combine(dest, "README.txt"),
            $"The Legend of Zelda: Ocarina of Time - save data exported by {Setup.AppName} on {DateTime.Now:f}\r\n\r\n" +
            "The \"save\" folder is the game's whole save data, as the game itself wrote it: account.sav, option.sav and every\r\n" +
            $"save slot ({string.Join(", ", slots.Select(Path.GetFileName))}). Restoring it replaces all slots on the console.\r\n\r\n" +
            "Importing it on a Switch 2 needs a homebrew save manager on the console (the kind JKSV is on the original Switch):\r\n" +
            "restore the contents of the \"save\" folder as the game's save data. Nintendo has no official way to copy a save from\r\n" +
            "a PC. Back up the console's own save first. This export has not been tried on a console: the PC version runs the\r\n" +
            "game's own save code, so the files are expected to match, but that is not confirmed.\r\n");
        var zip = dest + ".zip";
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dest, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        return (dest, zip, slots.Length);
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---------------------------------------------------------------- shared

    static IEnumerable<(Field, uint)> WalletChanges(int level)
    {
        // a bigger wallet replaces the smaller ones: each level below is owned too
        for (int i = 0; i < Wallets.Length; i++)
            yield return (Fields.Get($"IsGet.Item_Wallet_{Wallets[i]}"), i <= level ? 1u : 0u);
    }

    static int WalletOf(Func<string, bool> has)
    {
        for (int i = Wallets.Length - 1; i >= 0; i--)
            if (has($"Item_Wallet_{Wallets[i]}")) return i;
        return 0;
    }

    internal static IEnumerable<(Field, uint)> BottleChanges(int bottle, int contents)
    {
        if (bottle < 0 || contents < 0) throw new ArgumentException("Choose a bottle and what to put in it.");
        string b = BottleLetters[bottle];
        yield return (Fields.Get($"PlayerStatus.AssignableItem.BottleInfo.Bottle{b}.BottleItem"), Murmur.Hash(Fields.BottleContents[contents]));
        yield return (BottleValid(b), 1u);           // the bottle itself is owned
        yield return (Fields.Get(HasBottle), 1u);    // and Link has a bottle
    }

    const string HasBottle = "IsGet.Item_Bottle";
    static Field BottleValid(string letter) => Fields.Get($"PlayerStatus.AssignableItem.BottleInfo.Bottle{letter}.IsValid");

    /// <summary>
    /// Takes a bottle away: the slot is emptied and no longer owned. When it was the last owned bottle, "has a bottle"
    /// is cleared too. The "ever caught" log (IsGet.Item_Bottle_&lt;contents&gt;) is left as it is.
    /// </summary>
    internal static IEnumerable<(Field, uint)> BottleRemoval(int bottle, Func<Field, uint> get)
    {
        if (bottle < 0) throw new ArgumentException("Choose the bottle to remove.");
        string b = BottleLetters[bottle];
        if (get(BottleValid(b)) == 0) throw new InvalidOperationException($"Bottle {b} is not owned: nothing to remove.");
        var changes = new List<(Field, uint)>
        {
            (Fields.Get($"PlayerStatus.AssignableItem.BottleInfo.Bottle{b}.BottleItem"), Murmur.Hash("Empty")),
            (BottleValid(b), 0u),
        };
        if (!BottleLetters.Where(x => x != b).Any(x => get(BottleValid(x)) != 0)) changes.Add((Fields.Get(HasBottle), 0u));
        return changes;
    }

    static uint ParseRupees(string s)
    {
        if (!uint.TryParse(s.Trim(), out uint r) || r > 999) throw new ArgumentException("Rupees: a number from 0 to 999.");
        return r;
    }

    static string Hearts(uint quarters) => quarters % 4 == 0 ? (quarters / 4).ToString() : (quarters / 4.0).ToString("0.##");

    static string Short(Field f) => f.Name.Replace("PlayerStatus.AssignableItem.BottleInfo.", "").Replace("PlayerStatus.", "").Replace("IsGet.", "");

    static string Show(Field f, uint v) => f.Type switch
    {
        FieldType.Bool => v != 0 ? "yes" : "no",
        FieldType.Enum => Spaced(Fields.BottleContents.FirstOrDefault(x => Murmur.Hash(x) == v) ?? v.ToString("x8")),
        _ => f.Name.EndsWith("Life") ? Hearts(v) + " hearts" : v.ToString(),
    };

    static string Spaced(string s) => Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");

    static IEnumerable<string> ItemIds() => Fields.All.Where(f => f.Name.StartsWith("IsGet.") && f.Name != HasBottle).Select(f => f.Name[6..]);

    static string CategoryOf(string id) => id switch
    {
        _ when id.StartsWith("Weapon_") => "Weapons and shields",
        _ when id.StartsWith("Item_Melody_") => "Songs",
        _ when id.StartsWith("Item_Medal_") || id.StartsWith("Item_Stone_") => "Medallions and stones",
        _ when id.StartsWith("Item_Wallet_") || id.Contains("Bag_") || id.StartsWith("Item_Quiver_") || id.StartsWith("Item_Scale_") => "Upgrades and wallets",
        _ when id.StartsWith("Item_Dungeon_") => "Dungeon items",
        _ when id.Contains("_Mask_") => "Masks",
        _ when id.StartsWith("Armor_") => "Tunics, boots and gauntlets",
        _ when id.StartsWith("Item_") => "Items",
        _ => "Other",
    };

    static IEnumerable<string> Categories() => new[] { "Weapons and shields", "Items", "Upgrades and wallets", "Songs", "Medallions and stones", "Masks", "Tunics, boots and gauntlets", "Dungeon items", "Other" };

    static string Friendly(string id) => string.Join(" ", id.Split('_').Skip(1).Select(Spaced));

    static void FillItems(ListBox list, ComboBox category, Func<string, bool?> has)
    {
        var cat = category.SelectedItem as string;
        var keep = Selected(list).ToHashSet();
        list.Items.Clear();
        foreach (var id in ItemIds().Where(i => CategoryOf(i) == cat).OrderBy(Friendly))
        {
            bool? owned = null;
            try { owned = has(id); } catch { }
            var item = new ListBoxItem
            {
                Content = (owned == true ? "✓   " : "      ") + Friendly(id), Tag = id,
                Foreground = owned == true ? Theme.Good : Theme.Text,
            };
            list.Items.Add(item);
            if (keep.Contains(id)) item.IsSelected = true;
        }
    }

    static IEnumerable<string> Selected(ListBox list) => list.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList();

    void Guard(Action a, Action? onError = null)
    {
        try { a(); }
        catch (Exception ex)
        {
            onError?.Invoke();
            Log("Not done: " + ex.Message);
            MessageBox.Show(this, ex.Message, Setup.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Log(string s)
    {
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {s}\n");
        log.ScrollToEnd();
    }

    // ---------------------------------------------------------------- the game folder

    static string LoadGameDir()
    {
        foreach (var f in new[] { SettingsFile, OldSettingsFile })   // the old name's choice carries over
            try { var s = File.ReadAllLines(f)[0].Trim(); if (Directory.Exists(s)) return s; } catch { }
        return @"E:\Games\Ocarina of Time";
    }

    void PickGameDir()
    {
        var d = new OpenFolderDialog { Title = "The game's folder (Ocarina of Time's, or Hylian Triforce's AppData\\Local\\HylianTriforce\\Game)", InitialDirectory = gameDir };
        if (d.ShowDialog(this) != true) return;
        gameDir = d.FolderName;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, gameDir);
        }
        catch (Exception ex) { Log("The game folder is used now but couldn't be remembered for next time: " + ex.Message); }
        ShowGameDir();
    }

    void ShowGameDir()
    {
        gameDirText.Text = gameDir;
        gameDirText.Foreground = SaveFile.DataDir(gameDir) != null ? Theme.Text : Theme.Bad;
        LoadSlots();
    }
}
