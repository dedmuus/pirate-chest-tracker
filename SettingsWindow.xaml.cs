using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChestCounter;

public partial class SettingsWindow : Window
{
    readonly App app = App.Current;
    readonly bool loading = true;
    Button? capturingButton;

    AppSettings S => app.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = ChestArt.Render(64);
        MaxHeight = SystemParameters.WorkArea.Height; // never taller than the screen; the page scrolls instead

        XSlider.Maximum = Math.Max(200, SystemParameters.PrimaryScreenWidth - 120);
        YSlider.Maximum = Math.Max(200, SystemParameters.PrimaryScreenHeight - 80);
        ScaleSlider.Value = S.Scale;
        OpacitySlider.Value = S.Opacity;
        XSlider.Value = S.OffsetX;
        YSlider.Value = S.OffsetY;
        ShowToggle.IsChecked = S.ShowOverlay;
        loading = false;

        UpdateLayoutLabels();
        RefreshBindings();
        UpdateCount();

        FortniteOnlyToggle.IsChecked = S.OnlyWithFortnite;
        if (AppSettings.IsFirstRun) ShowSetup(true);
        // The overlay also shows while this window is open, so re-check when it opens or closes.
        // Deferred: showing another window while this one is still opening breaks its sizing.
        IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(app.UpdateOverlayVisibility,
            System.Windows.Threading.DispatcherPriority.Background);
        StateChanged += (_, _) => app.UpdateOverlayVisibility();

        NameBox.Text = S.SyncName;
        CodeBox.Text = FriendSync.FormatCode(S.SyncCode);
        SyncToggle.IsChecked = S.SyncEnabled;
        UpdateFriends();
        app.FriendsChanged += UpdateFriends;

        app.CountChanged += UpdateCount;
        app.OverlayVisibilityChanged += () => ShowToggle.IsChecked = S.ShowOverlay;
        SourceInitialized += (_, _) => DarkTitleBar();
        Deactivated += (_, _) => app.CancelCapture();
    }

    void DarkTitleBar()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int caption = 0x0017110F; // #0F1117 as COLORREF (BGR)
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    public sealed record GameRow(string Title, string When, int Chests, Brush Color);

    void UpdateCount()
    {
        BigCount.Text = S.Count.ToString();
        GameLabel.Text = $"Chests in game {S.Games.Count + 1}";

        bool any = S.Games.Count > 0;
        AvgStat.Text = any ? S.Average.ToString("0.#") : "–";
        BestStat.Text = any ? S.Best.ToString() : "–";
        GamesStat.Text = S.Games.Count.ToString();
        TotalStat.Text = S.Total.ToString();

        NoGames.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        GameScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        HistoryButtons.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        // Newest first; gold for games at or above your average.
        var accent = (Brush)FindResource("Accent");
        var rows = new List<GameRow>();
        for (int i = S.Games.Count - 1; i >= 0; i--)
        {
            GameRecord g = S.Games[i];
            rows.Add(new GameRow($"Game {i + 1}", FormatWhen(g.EndedAt), g.Chests,
                g.Chests >= S.Average ? accent : Brushes.White));
        }
        GameList.ItemsSource = rows;
    }

    static string FormatWhen(DateTime t)
    {
        if (t.Date == DateTime.Today) return $"Today, {t:h:mm tt}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"Yesterday, {t:h:mm tt}";
        return t.ToString("MMM d, h:mm tt");
    }

    void EndGame_Click(object sender, RoutedEventArgs e) => app.EndGame();
    void UndoGame_Click(object sender, RoutedEventArgs e) => app.UndoLastGame();

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDialog.Ask(this, "Clear history?",
                $"This deletes all {S.Games.Count} saved games and your averages. It can't be undone.",
                "Delete games", danger: true))
            app.ClearHistory();
    }

    // ---- counter buttons ----
    void Plus_Click(object sender, RoutedEventArgs e) => app.ChangeCount(+1);
    void Minus_Click(object sender, RoutedEventArgs e) => app.ChangeCount(-1);

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (S.Count == 0) return;
        if (ConfirmDialog.Ask(this, "Reset this game?",
                $"This sets the {S.Count} chests in this game back to 0 without saving them.",
                "Reset", danger: true))
            app.ResetCount();
    }

    // ---- hotkeys ----
    InputBinding Get(string tag) => tag switch
    {
        "Count" => S.CountKey,
        "Undo" => S.UndoKey,
        "EndGame" => S.EndGameKey,
        "Reset" => S.ResetKey,
        _ => S.ToggleKey
    };

    void Set(string tag, InputBinding b)
    {
        switch (tag)
        {
            case "Count": S.CountKey = b; break;
            case "Undo": S.UndoKey = b; break;
            case "EndGame": S.EndGameKey = b; break;
            case "Reset": S.ResetKey = b; break;
            default: S.ToggleKey = b; break;
        }
    }

    void RefreshBindings()
    {
        foreach (Button b in new[] { BindCount, BindUndo, BindEndGame, BindReset, BindToggle, SetupCount, SetupEndGame })
        {
            InputBinding binding = Get((string)b.Tag);
            b.Content = binding.DisplayName;
            b.Foreground = binding.IsNone ? (Brush)FindResource("Sub") : Brushes.White;
            b.ClearValue(BorderBrushProperty);
        }
    }

    void Bind_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        string tag = (string)button.Tag;

        app.CancelCapture();
        capturingButton = button;
        button.Content = "Press a key…";
        button.Foreground = (Brush)FindResource("Accent");
        button.BorderBrush = (Brush)FindResource("Accent");

        app.BeginCapture(input =>
        {
            capturingButton = null;
            if (!input.IsNone && !input.IsEscape)
            {
                // One input can only do one thing: unbind it anywhere else first.
                foreach (string other in new[] { "Count", "Undo", "EndGame", "Reset", "Toggle" })
                    if (Get(other) == input) Set(other, InputBinding.None);
                Set(tag, input);
                S.Save();
            }
            RefreshBindings();
        });
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        app.CancelCapture();
        Set((string)((Button)sender).Tag, InputBinding.None);
        S.Save();
        RefreshBindings();
    }

    // ---- friends ----
    public sealed record FriendRow(string Name, string Detail, int Count);

    void UpdateFriends()
    {
        FriendSync? sync = app.Sync;
        IReadOnlyList<FriendInfo> friends = S.SyncEnabled && sync != null ? sync.Friends : Array.Empty<FriendInfo>();

        string status = sync?.Status ?? "Off";
        if (!S.SyncEnabled) status = "Off";
        else if (sync?.IsConnected == true)
        {
            int online = 0;
            foreach (FriendInfo f in friends) if (f.Online) online++;
            status = friends.Count == 0 ? "Connected · waiting for your friend to join"
                : $"Connected · {online} of {friends.Count} friend{(friends.Count == 1 ? "" : "s")} online";
        }
        SyncStatus.Text = status;
        SyncDot.Fill = new SolidColorBrush(!S.SyncEnabled ? Color.FromRgb(0x6B, 0x70, 0x80)
            : sync?.IsConnected == true ? Color.FromRgb(0x4A, 0xDE, 0x80)
            : Color.FromRgb(0xF5, 0xC5, 0x42));

        var rows = new List<FriendRow>();
        foreach (FriendInfo f in friends)
        {
            string detail = $"{(f.Online ? "Online" : "Offline")} · Game {f.Games + 1}";
            if (f.Games > 0) detail += $" · Avg {f.Avg:0.#} · Best {f.Best} · Total {f.Total}";
            rows.Add(new FriendRow(f.Name, detail, f.Count));
        }
        FriendList.ItemsSource = rows;
    }

    /// <summary>Copies the name and code boxes into settings; returns true if the code changed.</summary>
    bool CommitSyncFields()
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) name = Environment.UserName;
        string code = FriendSync.FormatCode(CodeBox.Text);
        bool nameChanged = name != S.SyncName;
        bool codeChanged = FriendSync.NormalizeCode(code) != FriendSync.NormalizeCode(S.SyncCode);
        S.SyncName = name;
        S.SyncCode = code;
        NameBox.Text = name;
        CodeBox.Text = code;
        if (nameChanged && !codeChanged) app.PublishStats();
        return codeChanged;
    }

    void SyncField_Commit(object sender, RoutedEventArgs e)
    {
        if (CommitSyncFields()) app.ApplySync();
    }

    void SyncField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SyncField_Commit(sender, e);
    }

    void SyncToggle_Click(object sender, RoutedEventArgs e)
    {
        CommitSyncFields();
        if (SyncToggle.IsChecked == true && !FriendSync.IsValidCode(S.SyncCode))
        {
            S.SyncCode = FriendSync.NewCode();
            CodeBox.Text = S.SyncCode;
        }
        S.SyncEnabled = SyncToggle.IsChecked == true;
        app.ApplySync();
    }

    void NewCode_Click(object sender, RoutedEventArgs e)
    {
        S.SyncCode = FriendSync.NewCode();
        CodeBox.Text = S.SyncCode;
        app.ApplySync();
    }

    void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        CommitSyncFields();
        if (!FriendSync.IsValidCode(S.SyncCode)) return;
        try
        {
            Clipboard.SetText(S.SyncCode);
            CopyButton.Content = "Copied";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); CopyButton.Content = "Copy"; };
            t.Start();
        }
        catch (Exception)
        {
            // Clipboard can be briefly locked by another app.
        }
    }

    // ---- overlay ----
    void ShowToggle_Click(object sender, RoutedEventArgs e) => app.SetOverlayVisible(ShowToggle.IsChecked == true);

    void Layout_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        S.Scale = Math.Round(ScaleSlider.Value, 2);
        S.Opacity = Math.Round(OpacitySlider.Value, 2);
        S.OffsetX = (int)XSlider.Value;
        S.OffsetY = (int)YSlider.Value;
        UpdateLayoutLabels();
        app.ApplyLayout();
    }

    void UpdateLayoutLabels()
    {
        ScaleText.Text = $"{ScaleSlider.Value * 100:0}%";
        OpacityText.Text = $"{OpacitySlider.Value * 100:0}%";
        XText.Text = $"{(int)XSlider.Value}px";
        YText.Text = $"{(int)YSlider.Value}px";
    }

    // ---- window ----
    void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    void Quit_Click(object sender, RoutedEventArgs e) => app.Quit();
    // ---- first launch ----
    void ShowSetup(bool show)
    {
        SetupPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TabsBar.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        CounterPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SeasonPage.Visibility = Visibility.Collapsed;
        FooterTip.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetupDone_Click(object sender, RoutedEventArgs e)
    {
        app.CancelCapture();
        S.Save();
        ShowSetup(false);
        ShowTab(season: false);
    }

    // ---- updates ----
    string updateUrl = "";

    public void ShowUpdate(UpdateInfo update)
    {
        updateUrl = update.Url;
        UpdateText.Text = $"Chest Counter {update.Version} is out (you have {AppInfo.Version}). Your stats carry over.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    void Update_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(updateUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            UpdateText.Text = "Open " + updateUrl + " in your browser.";
        }
    }

    void FortniteOnlyToggle_Click(object sender, RoutedEventArgs e) =>
        app.SetOnlyWithFortnite(FortniteOnlyToggle.IsChecked == true);

    // ---- tabs ----
    void CounterTab_Click(object sender, RoutedEventArgs e) => ShowTab(season: false);
    void SeasonTab_Click(object sender, RoutedEventArgs e) => ShowTab(season: true);

    public void ShowTab(bool season)
    {
        CounterPage.Visibility = season ? Visibility.Collapsed : Visibility.Visible;
        SeasonPage.Visibility = season ? Visibility.Visible : Visibility.Collapsed;
        CounterTab.Style = (Style)FindResource(season ? "Btn" : "PrimaryBtn");
        SeasonTab.Style = (Style)FindResource(season ? "PrimaryBtn" : "Btn");
        if (season) SeasonPage.OnShown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing just hides to the tray; use Quit to exit.
        if (!app.IsQuitting)
        {
            e.Cancel = true;
            app.CancelCapture();
            Hide();
        }
        base.OnClosing(e);
    }
}
