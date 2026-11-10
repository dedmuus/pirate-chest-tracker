using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChestCounter;

public partial class SeasonPanel : UserControl
{
    static readonly (string Value, string Label)[] Platforms =
    {
        ("epic", "Epic account"), ("psn", "PlayStation"), ("xbl", "Xbox"),
    };

    public sealed record OddsRow(string Key, string Name, Brush NameBrush, string Expected, string Odds,
        string You, string Friend, string Either, bool Got);

    readonly App app = App.Current;
    bool loading;

    AppSettings S => app.Settings;

    public SeasonPanel()
    {
        InitializeComponent();
        NameBox.Text = S.EpicName;
        KeyBox.Text = S.FortniteApiKey;
        UpdatePlatformButton();

        // The panel lives as long as the main window, so these are never removed.
        app.CountChanged += Render;
        app.FriendsChanged += Render;
    }

    /// <summary>Called when the Season odds tab is shown.</summary>
    public async void OnShown()
    {
        Render();
        // Refresh if it's been a while.
        bool stale = S.SeasonFetchedAt == null || DateTime.Now - S.SeasonFetchedAt > TimeSpan.FromMinutes(10);
        if (stale && S.EpicName.Length > 0) await RefreshStats();
    }

    void KeyBox_TextChanged(object sender, TextChangedEventArgs e) =>
        KeyHint.Visibility = KeyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdatePlatformButton()
    {
        PlatformButton.Content = Platforms.FirstOrDefault(p => p.Value == S.EpicPlatform).Label ?? "Epic account";
    }

    void Platform_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.FindIndex(Platforms, p => p.Value == S.EpicPlatform);
        S.EpicPlatform = Platforms[(i + 1) % Platforms.Length].Value;
        UpdatePlatformButton();
        S.Save();
    }

    void Field_Commit(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        string key = KeyBox.Text.Trim();
        if (name != S.EpicName)
        {
            // A different account: its season numbers no longer apply.
            S.SeasonMatches = 0;
            S.SeasonWins = 0;
            S.SeasonFetchedAt = null;
        }
        S.EpicName = name;
        S.FortniteApiKey = key;
        S.Save();
        Render();
    }

    void GetKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(SeasonStats.KeyUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            StatusText.Text = "Open " + SeasonStats.KeyUrl + " in your browser.";
        }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStats();

    async System.Threading.Tasks.Task RefreshStats()
    {
        if (loading) return;
        Field_Commit(this, new RoutedEventArgs());
        loading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Foreground = (Brush)FindResource("Sub");
        StatusText.Text = "Looking up your season stats…";
        try
        {
            SeasonResult r = await SeasonStats.FetchAsync(S.EpicName, S.EpicPlatform, S.FortniteApiKey);
            S.SeasonMatches = r.Matches;
            S.SeasonWins = r.Wins;
            S.SeasonFetchedAt = DateTime.Now;
            app.PublishStats(); // saves, and shares your season matches with friends
            AppSettings.Log($"Season stats for {r.AccountName}: {r.Matches} matches, {r.Wins} wins");
        }
        catch (SeasonStatsException ex)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
            StatusText.Text = ex.Message;
            return;
        }
        finally
        {
            loading = false;
            RefreshButton.IsEnabled = true;
            Render();
        }
    }

    void Got_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        string key = (string)box.Tag;
        S.LootGot.Remove(key);
        if (box.IsChecked == true) S.LootGot.Add(key);
        S.Save();
        Render();
    }

    static string ExpectedText(string scope, double drops) => drops < 0.01
        ? $"{scope} · expected less than 0.01 drops"
        : $"{scope} · expected {drops:0.##} drops";

    void AutoToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = AutoToggle.IsChecked == true; // read first: Field_Commit re-renders and would reset the toggle
        Field_Commit(this, new RoutedEventArgs());
        app.SetAutoEndGame(on);
    }

    void NewWeek_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Start a new week?",
                "Weekly items restart from 0 chests and their Got it switches are cleared. " +
                "Pirate Shop Access and other season items keep counting the whole season.",
                "Start new week"))
            return;

        S.WeekStartTotal = S.Total;
        S.WeekStartedAt = DateTime.Now;
        foreach (LootItem item in Loot.Table)
            if (item.Weekly) S.LootGot.Remove(item.Key);
        app.PublishStats(); // saves, and tells friends your week restarted
        Render();
    }

    void Render()
    {
        bool have = S.SeasonFetchedAt != null;
        if (have && StatusText.Foreground is SolidColorBrush b && b.Color != Color.FromRgb(0xFF, 0x8A, 0x8A))
            StatusText.Text = $"Updated {S.SeasonFetchedAt:MMM d, h:mm tt} for {S.EpicName}";
        else if (!have && !loading && StatusText.Text.Length == 0)
            StatusText.Text = "Enter your Epic name, then click Refresh stats.";

        double myChests = S.SeasonChests;
        MatchesStat.Text = have ? S.SeasonMatches.ToString("N0") : "–";
        WinsStat.Text = have ? S.SeasonWins.ToString("N0") : "–";
        AvgStat.Text = S.Games.Count > 0 ? S.Average.ToString("0.#") : "–";
        ChestsStat.Text = have && S.Games.Count > 0 ? $"~{myChests:N0}" : "–";

        // First friend that has shared season data.
        FriendInfo? friend = S.SyncEnabled && app.Sync != null
            ? app.Sync.Friends.FirstOrDefault(f => f.SeasonMatches > 0 && f.Games > 0)
            : null;
        double friendChests = friend?.SeasonChests ?? 0;

        if (friend != null)
            FriendLine.Text = $"{friend.Name}: {friend.SeasonMatches:N0} matches × {friend.Avg:0.#} chests per game ≈ {friendChests:N0} chests this season";
        else if (S.SyncEnabled)
            FriendLine.Text = "Your friend's odds show here once they've refreshed their season stats with sharing on.";
        else
            FriendLine.Text = "Turn on Share with friends to see your friend's odds too.";
        FriendLine.Visibility = Visibility.Visible;

        FriendHeader.Text = friend?.Name ?? "Friend";
        Visibility friendCols = friend != null ? Visibility.Visible : Visibility.Hidden;
        FriendHeader.Visibility = friendCols;
        EitherHeader.Visibility = friendCols;

        AutoToggle.IsChecked = S.AutoEndGame;
        AutoText.Text = S.AutoEndGame && app.AutoStatus.Length > 0
            ? app.AutoStatus
            : "Saves each game for you when Fortnite records the match.";
        AutoText.ToolTip = "Chests you open in the next game stay in the next game, even though the stats arrive a few minutes late. Your End game button still works too.";

        LootTitle.Text = $"Loot odds · {Loot.Season}";

        // Weekly items use chests counted since New week; until then they fall back to the season estimate.
        int? week = S.WeekChests;
        WeekText.Text = week != null
            ? $"This week: {week:N0} chests counted since {S.WeekStartedAt:ddd MMM d, h:mm tt}."
            : "Weekly items use your whole season until you press New week.";
        bool seasonReady = have && S.Games.Count > 0;
        double friendWeek = friend == null ? 0 : friend.WeekChests >= 0 ? friend.WeekChests : friendChests;

        var gold = (Brush)FindResource("Accent");
        var rows = new List<OddsRow>();
        foreach (LootItem item in Loot.Table)
        {
            bool useWeek = item.Weekly && week != null;
            bool ready = useWeek || seasonReady;
            double mine = useWeek ? week!.Value : seasonReady ? myChests : 0;
            double theirs = item.Weekly ? friendWeek : friendChests;
            string scope = !item.Weekly ? "Whole season" : useWeek ? "This week" : "Season so far";

            double you = Loot.Chance(item.OneIn, mine);
            double them = Loot.Chance(item.OneIn, theirs);
            double either = 1 - (1 - you) * (1 - them);
            bool got = S.LootGot.Contains(item.Key);
            string expected = got ? (item.Weekly ? "You got this one this week!" : "You got this one!")
                : ready ? ExpectedText(scope, mine / item.OneIn)
                : scope;
            rows.Add(new OddsRow(item.Key, item.Name, got ? gold : Brushes.White, expected, $"1 in {item.OneIn:N0}",
                ready ? Loot.Percent(you) : "–",
                friend != null ? Loot.Percent(them) : "",
                friend != null ? Loot.Percent(either) : "",
                got));
        }
        OddsList.ItemsSource = rows;
    }
}
