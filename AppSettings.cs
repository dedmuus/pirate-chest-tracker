using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace ChestCounter;

public sealed record GameRecord(int Chests, DateTime EndedAt);

public sealed class AppSettings
{
    /// <summary>Chests in the current (unsaved) game.</summary>
    public int Count { get; set; }
    /// <summary>When each chest in the current game was counted (UTC), oldest first. Used by automatic End game.</summary>
    public List<DateTime> ChestTimes { get; set; } = new();
    /// <summary>Save games automatically when Fortnite-API shows a new match (off by default).</summary>
    public bool AutoEndGame { get; set; }
    /// <summary>Finished games, oldest first.</summary>
    public List<GameRecord> Games { get; set; } = new();
    public InputBinding CountKey { get; set; } = new(InputKind.Key, 0x77);  // F8
    public InputBinding UndoKey { get; set; } = new(InputKind.Key, 0x76);   // F7
    public InputBinding EndGameKey { get; set; } = new(InputKind.Key, 0x78); // F9
    public InputBinding ResetKey { get; set; } = InputBinding.None;
    public InputBinding ToggleKey { get; set; } = new(InputKind.Key, 0x79); // F10

    [JsonIgnore] public int Total => Games.Sum(g => g.Chests) + Count;
    [JsonIgnore] public double Average => Games.Count == 0 ? 0 : Games.Average(g => g.Chests);
    [JsonIgnore] public int Best => Games.Count == 0 ? 0 : Games.Max(g => g.Chests);
    public bool ShowOverlay { get; set; } = true;
    /// <summary>Hide the overlay unless Fortnite is running (or this window is open).</summary>
    public bool OnlyWithFortnite { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public int OffsetX { get; set; } = 24;
    public int OffsetY { get; set; } = 24;

    // Friend sharing (off by default)
    public bool SyncEnabled { get; set; }
    public string SyncName { get; set; } = Environment.UserName;
    public string SyncCode { get; set; } = "";
    public string SyncClientId { get; set; } = "";

    // Season stats from Fortnite-API.com
    public string EpicName { get; set; } = "";
    public string EpicPlatform { get; set; } = "epic";
    public string FortniteApiKey { get; set; } = "";
    public int SeasonMatches { get; set; }
    public int SeasonWins { get; set; }
    public DateTime? SeasonFetchedAt { get; set; }

    /// <summary>Your total chests when you pressed New week; null until the first New week.</summary>
    public int WeekStartTotal { get; set; }
    public DateTime? WeekStartedAt { get; set; }

    /// <summary>Chests counted since New week, or null if no week has been started.</summary>
    [JsonIgnore] public int? WeekChests => WeekStartedAt == null ? null : Math.Max(0, Total - WeekStartTotal);

    /// <summary>Loot table items you've marked as received (by key).</summary>
    public List<string> LootGot { get; set; } = new();

    /// <summary>Estimated chests this season: season matches × your average chests per counted game.</summary>
    [JsonIgnore] public double SeasonChests => SeasonMatches * Average;

    /// <summary>Set by "--profile NAME" so a second test copy uses its own folder.</summary>
    public static string Profile { get; set; } = "";

    static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Profile.Length == 0 ? "ChestCounter" : "ChestCounter-" + Profile);
    public static string LootCachePath => Path.Combine(Folder, "loot-cache.json");

    /// <summary>True when there was no settings file yet, i.e. a first launch.</summary>
    public static bool IsFirstRun { get; private set; }

    static string FilePath => Path.Combine(Folder, "settings.json");
    static string BackupPath => Path.Combine(Folder, "settings.backup.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Set when saved data existed but couldn't be read, so the app can tell the user.</summary>
    public static string? LoadError { get; private set; }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Reads a settings file, retrying for a few seconds in case another copy is still saving it.</summary>
    static AppSettings? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        for (int attempt = 1; attempt <= 15; attempt++)
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
                if (s != null) return s;
                Log($"{Path.GetFileName(path)} was empty");
                return null;
            }
            catch (IOException e)
            {
                Log($"{Path.GetFileName(path)} busy (attempt {attempt}): {e.Message}");
                Thread.Sleep(200);
            }
            catch (Exception e)
            {
                Log($"{Path.GetFileName(path)} unreadable: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }
        return null;
    }

    public static AppSettings Load()
    {
        IsFirstRun = !File.Exists(FilePath) && !File.Exists(BackupPath);
        AppSettings? s = TryRead(FilePath);
        if (s == null && File.Exists(BackupPath))
        {
            s = TryRead(BackupPath);
            if (s != null) Log("Loaded from backup file");
        }
        if (s == null && File.Exists(FilePath))
        {
            // Unreadable and no usable backup: keep a copy so nothing is overwritten for good.
            string copy = Path.Combine(Folder, $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(FilePath, copy);
            }
            catch (Exception)
            {
            }
            LoadError = $"Chest Counter couldn't read your saved stats, so it started fresh.\n\nA copy of the old file was kept here:\n{copy}";
        }
        s ??= new();
        Log($"Started: {s.Games.Count} saved games, {s.Count} chests in current game");
        s.Games ??= new();
        s.Count = Math.Max(0, s.Count);
        // Keep one time per chest (older versions didn't record them; treat those as "long ago").
        s.ChestTimes ??= new();
        while (s.ChestTimes.Count < s.Count) s.ChestTimes.Insert(0, DateTime.MinValue);
        if (s.ChestTimes.Count > s.Count) s.ChestTimes.RemoveRange(0, s.ChestTimes.Count - s.Count);
        s.Scale = Math.Clamp(s.Scale, 0.5, 2.5);
        s.Opacity = Math.Clamp(s.Opacity, 0.3, 1.0);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // Write to a temp file, then swap it in, so a crash or power cut mid-save
            // can't leave a half-written file. The previous version is kept as the backup.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath);
            else File.Move(temp, FilePath);
        }
        catch (Exception e)
        {
            // Never crash the overlay over a failed save, but record it.
            Log($"Save failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
