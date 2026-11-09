using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ChestCounter;

public enum CountChange { None, Add, Remove, Reset, Bank }

public partial class App : Application
{
    string ShowEventName => @"Local\ChestCounter.Show" + AppSettings.Profile;
    FriendSync? sync;
    public FriendSync? Sync => sync;
    public event Action? FriendsChanged;
    Mutex? singleInstance;
    bool ownsInstance;
    EventWaitHandle? showEvent;

    static bool OtherCopyRunning()
    {
        int me = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("ChestCounter"))
        {
            using (p)
                if (p.Id != me && !p.HasExited) return true;
        }
        return false;
    }

    bool TryAcquireInstance(int timeoutMs)
    {
        try
        {
            return singleInstance!.WaitOne(timeoutMs);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous copy was killed; we own the lock now
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
    InputHook? hook;
    Forms.NotifyIcon? tray;
    Forms.ToolStripMenuItem? trayToggleItem;
    OverlayWindow? overlay;
    SettingsWindow? settingsWindow;
    Action<InputBinding>? capture;

    public AppSettings Settings { get; private set; } = new();
    public bool IsQuitting { get; private set; }
    public event Action? CountChanged;
    public event Action? OverlayVisibilityChanged;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--export-icon")
        {
            ChestArt.WriteIco(e.Args[1]);
            Shutdown();
            return;
        }

        // Testing aid: "--profile NAME" runs a separate copy with its own settings folder.
        int profileArg = Array.IndexOf(e.Args, "--profile");
        if (profileArg >= 0 && profileArg + 1 < e.Args.Length)
            AppSettings.Profile = new string(e.Args[profileArg + 1].Where(char.IsLetterOrDigit).ToArray());

        AppSettings.Log($"Launch: pid {Environment.ProcessId} from {Environment.ProcessPath}");
        DispatcherUnhandledException += (_, ex) => AppSettings.Log($"Error: {ex.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => AppSettings.Log($"Fatal: {ex.ExceptionObject}");

        singleInstance = new Mutex(false, @"Local\ChestCounter.SingleInstance" + AppSettings.Profile);
        if (!TryAcquireInstance(0))
        {
            AppSettings.Log("Another copy holds the lock");
            // Another copy holds the lock. If it's running, ask it to show its window.
            bool signaled = false;
            try
            {
                AllowSetForegroundWindow(-1);
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
                signaled = true;
            }
            catch (Exception)
            {
            }

            // If it was just closing, it releases the lock only after its final save.
            // Wait for that, then start normally so we read the freshly saved stats.
            if (TryAcquireInstance(5000))
            {
                AppSettings.Log("Lock acquired after waiting");
                ownsInstance = true;
            }
            else if (OtherCopyRunning())
            {
                AppSettings.Log($"Other copy still running (signaled={signaled}); exiting");
                singleInstance.Dispose();
                singleInstance = null;
                Shutdown();
                return;
            }
            else
            {
                // Nobody is actually running; the lock is stale. Start anyway.
                AppSettings.Log("Lock busy but no other copy running; starting anyway");
            }
        }
        else
        {
            ownsInstance = true;
        }

        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                try { Dispatcher.Invoke(ShowSettings); }
                catch (Exception) { return; } // app is shutting down
            }
        }) { IsBackground = true }.Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Settings = AppSettings.Load();
        if (string.IsNullOrEmpty(Settings.SyncClientId))
        {
            Settings.SyncClientId = Guid.NewGuid().ToString("N")[..12];
            Settings.Save();
        }
        if (string.IsNullOrWhiteSpace(Settings.SyncName)) Settings.SyncName = Environment.UserName;

        overlay = new OverlayWindow();
        overlay.ApplyLayout(Settings);
        overlay.SetCount(Settings, CountChange.None);
        fortniteWatch.Tick += (_, _) => UpdateOverlayVisibility();
        fortniteWatch.Start();
        autoTimer.Tick += async (_, _) => await AutoTick();
        autoTimer.Start();
        if (Settings.AutoEndGame) AutoStatus = "Waiting for the first check…";

        sync = new FriendSync(Dispatcher);
        sync.Changed += RefreshFriends;
        if (Settings.SyncEnabled && FriendSync.IsValidCode(Settings.SyncCode)) sync.Start(Settings);

        hook = new InputHook(Dispatcher);
        hook.Pressed += OnInput;
        hook.Start();

        CreateTray();
        ShowSettings();

        // Season loot table from GitHub, then a quiet check for a newer version.
        _ = InitOnlineAsync();

        if (AppSettings.LoadError != null)
            ConfirmDialog.Tell(settingsWindow, "Couldn't read your stats", AppSettings.LoadError);
    }

    void OnInput(InputBinding input)
    {
        if (capture != null)
        {
            if (input.Kind == InputKind.Mouse && input.Code is (int)MouseBtn.Left or (int)MouseBtn.Right)
            {
                // Left/right click are fire/aim in-game, so they can't be bound. Left click cancels.
                if (input.Code == (int)MouseBtn.Left) CancelCapture();
                return;
            }
            Action<InputBinding> done = capture;
            capture = null;
            done(input);
            return;
        }

        if (input == Settings.CountKey) ChangeCount(+1);
        else if (input == Settings.UndoKey) ChangeCount(-1);
        else if (input == Settings.EndGameKey) EndGame();
        else if (input == Settings.ResetKey) ResetCount();
        else if (input == Settings.ToggleKey) ToggleOverlay();
    }

    /// <summary>The next key/mouse button pressed is passed to <paramref name="onCaptured"/> (None if cancelled).</summary>
    public void BeginCapture(Action<InputBinding> onCaptured)
    {
        CancelCapture();
        capture = onCaptured;
    }

    public void CancelCapture()
    {
        Action<InputBinding>? pending = capture;
        capture = null;
        pending?.Invoke(InputBinding.None);
    }

    public void ChangeCount(int delta)
    {
        int next = Math.Max(0, Settings.Count + delta);
        if (next == Settings.Count) return;
        Settings.Count = next;
        if (delta > 0) Settings.ChestTimes.Add(DateTime.UtcNow);
        else if (Settings.ChestTimes.Count > 0) Settings.ChestTimes.RemoveAt(Settings.ChestTimes.Count - 1);
        Changed(delta > 0 ? CountChange.Add : CountChange.Remove);
    }

    /// <summary>Sets the current game back to 0 without saving it.</summary>
    public void ResetCount()
    {
        Settings.Count = 0;
        Settings.ChestTimes.Clear();
        Changed(CountChange.Reset);
    }

    DateTime lastBank = DateTime.MinValue;

    /// <summary>Saves the current game's chests to history and starts a new game.</summary>
    public void EndGame()
    {
        // Ignore an accidental double press.
        if ((DateTime.Now - lastBank).TotalSeconds < 5) return;
        lastBank = DateTime.Now;
        Settings.Games.Add(new GameRecord(Settings.Count, DateTime.Now));
        Settings.Count = 0;
        Settings.ChestTimes.Clear();
        if (Settings.SeasonFetchedAt != null) Settings.SeasonMatches++; // counts now; confirmed by the API below
        autoEnd.NoteManualEnd(DateTime.UtcNow); // so automatic End game doesn't save this match again
        Changed(CountChange.Bank);
        if (!Settings.AutoEndGame) ScheduleSeasonRefresh(); // the auto checker refreshes on its own
    }

    // ---- automatic End game ----

    readonly AutoEndGame autoEnd = new();
    readonly System.Windows.Threading.DispatcherTimer autoTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    DateTime fortniteSeenAt = DateTime.MinValue;
    bool autoChecking;

    /// <summary>Shown under the toggle on the Season odds tab.</summary>
    public string AutoStatus { get; private set; } = "";

    public void SetAutoEndGame(bool on)
    {
        Settings.AutoEndGame = on;
        Settings.Save();
        autoEnd.Reset();
        AutoStatus = on ? "Starting…" : "";
        CountChanged?.Invoke();
        if (on) _ = AutoTick();
    }

    async System.Threading.Tasks.Task AutoTick()
    {
        if (!Settings.AutoEndGame || autoChecking) return;
        if (Settings.EpicName.Length == 0 || Settings.FortniteApiKey.Length == 0)
        {
            SetAutoStatus("Needs your Epic name and API key above.");
            return;
        }

        // Check while Fortnite is open, and for 10 minutes after it closes (the last match's stats arrive late).
        if (FortniteRunning()) fortniteSeenAt = DateTime.Now;
        else if (DateTime.Now - fortniteSeenAt > TimeSpan.FromMinutes(10))
        {
            autoEnd.Reset();
            SetAutoStatus("Waiting for Fortnite to open.");
            return;
        }

        autoChecking = true;
        try
        {
            SeasonResult r = await SeasonStats.FetchAsync(Settings.EpicName, Settings.EpicPlatform, Settings.FortniteApiKey);
            DateTime now = DateTime.UtcNow;
            DateTime? endAt = autoEnd.OnPoll(r.Matches, r.LastModifiedUtc, now, out int newMatches);
            if (endAt is DateTime end) SaveGameAt(end, newMatches, r.LastModifiedUtc != null);

            // Season numbers: the API's count plus any End game presses it hasn't caught up with yet.
            Settings.SeasonMatches = r.Matches + autoEnd.PendingManualEnds(now);
            Settings.SeasonWins = r.Wins;
            Settings.SeasonFetchedAt = DateTime.Now;
            PublishStats();
            SetAutoStatus($"On · last checked {DateTime.Now:h:mm tt} ({r.Matches:N0} matches this season)");
        }
        catch (SeasonStatsException e)
        {
            SetAutoStatus(e.Message);
        }
        finally
        {
            autoChecking = false;
        }
    }

    /// <summary>Saves the chests counted up to <paramref name="endUtc"/> as a finished game; later ones stay in the new game.</summary>
    void SaveGameAt(DateTime endUtc, int matches, bool exactEnd)
    {
        (int before, List<DateTime> after) = AutoEndGame.Split(Settings.ChestTimes, endUtc);
        Settings.Games.Add(new GameRecord(before, endUtc.ToLocalTime()));
        Settings.ChestTimes = after;
        Settings.Count = after.Count;
        lastBank = DateTime.Now;
        AppSettings.Log($"Auto End game: saved {before} chests (match ended {endUtc.ToLocalTime():h:mm:ss tt}" +
            $"{(exactEnd ? "" : ", estimated")}), {after.Count} kept for the next game" +
            (matches > 1 ? $"; {matches} matches finished since the last check, saved as one game" : ""));
        Changed(CountChange.Bank);
    }

    void SetAutoStatus(string status)
    {
        AutoStatus = status;
        CountChanged?.Invoke(); // re-renders the Season odds tab
    }

    System.Windows.Threading.DispatcherTimer? seasonTimer;
    int seasonRefreshStep;

    /// <summary>
    /// Re-reads season matches from Fortnite-API after a game. Their stats lag behind the game,
    /// so check at 3 minutes (never lowering the count) and again at 10 minutes (trusting the API).
    /// </summary>
    void ScheduleSeasonRefresh()
    {
        if (Settings.EpicName.Length == 0 || Settings.FortniteApiKey.Length == 0) return;
        seasonTimer?.Stop();
        seasonRefreshStep = 0;
        seasonTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        seasonTimer.Tick += async (_, _) =>
        {
            seasonRefreshStep++;
            bool final = seasonRefreshStep >= 2;
            if (final) seasonTimer!.Stop();
            else seasonTimer!.Interval = TimeSpan.FromMinutes(7);
            await RefreshSeasonQuietly(trustApi: final);
        };
        seasonTimer.Start();
    }

    async System.Threading.Tasks.Task RefreshSeasonQuietly(bool trustApi)
    {
        try
        {
            SeasonResult r = await SeasonStats.FetchAsync(Settings.EpicName, Settings.EpicPlatform, Settings.FortniteApiKey);
            Settings.SeasonMatches = trustApi ? r.Matches : Math.Max(r.Matches, Settings.SeasonMatches);
            Settings.SeasonWins = r.Wins;
            Settings.SeasonFetchedAt = DateTime.Now;
            PublishStats();
            CountChanged?.Invoke(); // re-renders the Season odds tab
            AppSettings.Log($"Season auto-refresh: {Settings.SeasonMatches} matches (API {r.Matches})");
        }
        catch (SeasonStatsException e)
        {
            AppSettings.Log($"Season auto-refresh failed: {e.Message}");
        }
    }

    /// <summary>Puts the last saved game back as the current game.</summary>
    public void UndoLastGame()
    {
        if (Settings.Games.Count == 0) return;
        GameRecord last = Settings.Games[^1];
        Settings.Games.RemoveAt(Settings.Games.Count - 1);
        Settings.Count += last.Chests;
        // Those chests came first; mark them as before any match end.
        Settings.ChestTimes.InsertRange(0, Enumerable.Repeat(DateTime.MinValue, last.Chests));
        lastBank = DateTime.MinValue;
        Changed(CountChange.None);
    }

    public void ClearHistory()
    {
        Settings.Games.Clear();
        Changed(CountChange.None);
    }

    void Changed(CountChange change)
    {
        overlay?.SetCount(Settings, change);
        Settings.Save();
        if (sync?.IsRunning == true) sync.PublishStats(Settings);
        CountChanged?.Invoke();
    }

    /// <summary>Starts or stops sharing to match the current settings.</summary>
    public void ApplySync()
    {
        Settings.Save();
        if (Settings.SyncEnabled && FriendSync.IsValidCode(Settings.SyncCode)) sync?.Start(Settings);
        else sync?.Stop();
        RefreshFriends();
    }

    /// <summary>Sends your stats again, e.g. after a name change.</summary>
    public void PublishStats()
    {
        Settings.Save();
        if (sync?.IsRunning == true) sync.PublishStats(Settings);
    }

    void RefreshFriends()
    {
        overlay?.SetFriends(Settings.SyncEnabled && sync != null ? sync.Friends : Array.Empty<FriendInfo>());
        FriendsChanged?.Invoke();
    }

    public void SetOverlayVisible(bool visible)
    {
        Settings.ShowOverlay = visible;
        if (trayToggleItem != null) trayToggleItem.Checked = visible;
        Settings.Save();
        UpdateOverlayVisibility();
        OverlayVisibilityChanged?.Invoke();
    }

    public void SetOnlyWithFortnite(bool only)
    {
        Settings.OnlyWithFortnite = only;
        Settings.Save();
        UpdateOverlayVisibility();
    }

    readonly System.Windows.Threading.DispatcherTimer fortniteWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    static bool FortniteRunning()
    {
        System.Diagnostics.Process[] found = System.Diagnostics.Process.GetProcessesByName("FortniteClient-Win64-Shipping");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    /// <summary>Shows the overlay if it's switched on and (when limited to Fortnite) the game or this window is open.</summary>
    public void UpdateOverlayVisibility()
    {
        if (overlay == null) return;
        bool windowOpen = settingsWindow?.IsVisible == true && settingsWindow.WindowState != WindowState.Minimized;
        bool want = Settings.ShowOverlay && (!Settings.OnlyWithFortnite || windowOpen || FortniteRunning());
        if (want && !overlay.IsVisible) overlay.Show();
        else if (!want && overlay.IsVisible) overlay.Hide();
    }

    public void ToggleOverlay() => SetOverlayVisible(!Settings.ShowOverlay);

    public void ApplyLayout()
    {
        overlay?.ApplyLayout(Settings);
        Settings.Save();
    }

    public void ShowSettings()
    {
        settingsWindow ??= new SettingsWindow();
        settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    async System.Threading.Tasks.Task InitOnlineAsync()
    {
        await Loot.LoadAsync(AppSettings.LootCachePath);
        CountChanged?.Invoke(); // re-render odds with the latest table
        UpdateInfo? update = await UpdateCheck.GetNewerAsync();
        if (update != null) settingsWindow?.ShowUpdate(update);
    }

    public void ShowSeason()
    {
        ShowSettings();
        settingsWindow!.ShowTab(season: true);
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Chest Counter", null, (_, _) => ShowSettings());
        menu.Items.Add("Season odds", null, (_, _) => ShowSeason());
        menu.Items.Add("End game (save chests)", null, (_, _) => EndGame());
        menu.Items.Add("Reset this game", null, (_, _) => ResetCount());
        trayToggleItem = new Forms.ToolStripMenuItem("Show overlay", null, (_, _) => ToggleOverlay())
        {
            Checked = Settings.ShowOverlay
        };
        menu.Items.Add(trayToggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        tray = new Forms.NotifyIcon
        {
            Icon = ChestArt.CreateTrayIcon(),
            Text = "Chest Counter",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowSettings();
        };
    }

    public void Quit()
    {
        IsQuitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        hook?.Dispose();
        sync?.Dispose(); // marks you offline for friends; done before releasing the lock below
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        if (overlay != null)
        {
            Settings.Save();
            AppSettings.Log($"Quit: saved {Settings.Games.Count} games, {Settings.Count} chests in current game");
        }
        // Release the lock only after saving, so a new copy never reads a half-finished save.
        if (ownsInstance) singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
