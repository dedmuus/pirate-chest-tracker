using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChestCounter;

public sealed class SeasonStatsException : Exception
{
    public SeasonStatsException(string message) : base(message) { }
}

/// <param name="LastModifiedUtc">When these stats last changed, i.e. roughly when the last match ended.</param>
public sealed record SeasonResult(string AccountName, int Matches, int Wins, DateTime? LastModifiedUtc = null);

/// <summary>Current-season Battle Royale stats from Fortnite-API.com.</summary>
public static class SeasonStats
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public const string KeyUrl = "https://dash.fortnite-api.com/account";

    public static async Task<SeasonResult> FetchAsync(string epicName, string platform, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(epicName)) throw new SeasonStatsException("Enter your Epic name first.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new SeasonStatsException("Paste your free Fortnite-API key first.");

        string url = "https://fortnite-api.com/v2/stats/br/v2"
            + $"?name={Uri.EscapeDataString(epicName.Trim())}&accountType={Uri.EscapeDataString(platform)}&timeWindow=season";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());

        HttpResponseMessage response;
        string body;
        try
        {
            response = await Http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SeasonStatsException("Couldn't reach Fortnite-API.com. Check your internet and try again.");
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                string apiError = "";
                try { apiError = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() ?? ""; }
                catch (Exception) { }

                throw new SeasonStatsException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "That API key isn't valid. Copy it again from dash.fortnite-api.com.",
                    HttpStatusCode.Forbidden => "Those stats are private. In Fortnite: Settings › Account and Privacy › turn on \"Show on Career Leaderboard\", then try again.",
                    HttpStatusCode.NotFound => $"No account called \"{epicName.Trim()}\" on that platform.",
                    (HttpStatusCode)429 => "Too many lookups. Wait a minute and try again.",
                    _ => $"Fortnite-API.com error {(int)response.StatusCode}: {apiError}"
                });
            }

            try
            {
                JsonElement data = JsonDocument.Parse(body).RootElement.GetProperty("data");
                string name = data.GetProperty("account").GetProperty("name").GetString() ?? epicName;
                JsonElement stats = data.GetProperty("stats").GetProperty("all");
                if (stats.ValueKind == JsonValueKind.Null) return new SeasonResult(name, 0, 0);
                JsonElement overall = stats.GetProperty("overall");
                DateTime? modified = null;
                if (overall.TryGetProperty("lastModified", out JsonElement lm) && lm.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lm.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out DateTime parsed))
                    modified = parsed;
                return new SeasonResult(name, overall.GetProperty("matches").GetInt32(), overall.GetProperty("wins").GetInt32(), modified);
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                throw new SeasonStatsException("Fortnite-API.com sent stats in an unexpected format.");
            }
        }
    }
}

/// <param name="Weekly">True if the item has a fresh chance every week; false if it's once per season.</param>
public sealed record LootItem(string Key, string Name, double OneIn, bool Weekly = false);

public static class Loot
{
    sealed record LootFile(string Season, List<LootItem> Items);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Built-in odds per chest opened; replaced by loot.json from GitHub when available.
    public static IReadOnlyList<LootItem> Table { get; private set; } = new[]
    {
        new LootItem("gold-outfit", "Weekly Gold Outfit", 1_562_500, Weekly: true),
        new LootItem("pirate-shop", "Pirate Shop Access", 137_500, Weekly: false),
        new LootItem("vehicle-decal", "Weekly Vehicle & Decal", 7_813, Weekly: true),
        new LootItem("pickaxe-backbling", "Weekly Pickaxe or Backbling", 568, Weekly: true),
        new LootItem("jam-track", "Weekly Jam Track", 391, Weekly: true),
        new LootItem("loading-screen", "Weekly Loading Screen", 272, Weekly: true),
    };

    public static string Season { get; private set; } = "Chapter 7 Season 4";

    /// <summary>Uses the cached table, then downloads the latest loot.json from the repo.</summary>
    public static async Task LoadAsync(string cachePath)
    {
        try
        {
            if (System.IO.File.Exists(cachePath)) Apply(await System.IO.File.ReadAllTextAsync(cachePath));
        }
        catch (Exception e)
        {
            AppSettings.Log($"Loot cache unreadable: {e.Message}");
        }

        if (!AppInfo.HasRepo) return;
        try
        {
            string json = await Http.GetStringAsync($"https://raw.githubusercontent.com/{AppInfo.RepoSlug}/main/loot.json");
            if (Apply(json)) await System.IO.File.WriteAllTextAsync(cachePath, json);
        }
        catch (Exception e)
        {
            AppSettings.Log($"Loot download failed, using {Season} table: {e.Message}");
        }
    }

    static bool Apply(string json)
    {
        LootFile? file = JsonSerializer.Deserialize<LootFile>(json, Json);
        if (file?.Items == null || file.Items.Count == 0 || file.Items.Exists(i => i.OneIn < 1 || i.Key.Length == 0)) return false;
        Table = file.Items;
        Season = file.Season;
        return true;
    }

    /// <summary>Chance of getting the item at least once in <paramref name="chests"/> chests.</summary>
    public static double Chance(double oneIn, double chests) =>
        chests <= 0 ? 0 : -double.ExpM1(chests * double.LogP1(-1.0 / oneIn));

    public static string Percent(double p) => p switch
    {
        <= 0 => "0%",
        < 0.0001 => "<0.01%",
        < 0.01 => $"{p * 100:0.00}%",
        > 0.999 => ">99.9%",
        _ => $"{p * 100:0.#}%"
    };
}
