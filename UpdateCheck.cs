using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChestCounter;

public sealed record UpdateInfo(string Version, string Url);

/// <summary>Checks GitHub Releases for a newer version.</summary>
public static class UpdateCheck
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChestCounter/" + AppInfo.Version); // GitHub requires a User-Agent
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Returns the newer release, or null if up to date, offline, or no repo is set.</summary>
    public static async Task<UpdateInfo?> GetNewerAsync()
    {
        if (!AppInfo.HasRepo) return null;
        try
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{AppInfo.RepoSlug}/releases/latest");
            JsonElement root = JsonDocument.Parse(json).RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl + "/releases";
            if (Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest)
                && Version.TryParse(AppInfo.Version, out Version? current)
                && latest > current)
                return new UpdateInfo(latest.ToString(3), url);
        }
        catch (Exception e)
        {
            AppSettings.Log($"Update check failed: {e.Message}");
        }
        return null;
    }
}
