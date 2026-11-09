using System.Reflection;

namespace ChestCounter;

public static class AppInfo
{
    /// <summary>
    /// GitHub "owner/repo" this app is published from. Used for the update check and the
    /// season loot table. Leave as-is until the repo exists; both features stay off until then.
    /// </summary>
    public const string RepoSlug = "dedmuus/pirate-chest-tracker";

    public static bool HasRepo => !RepoSlug.StartsWith("YOUR-GITHUB-NAME");

    public static string RepoUrl => $"https://github.com/{RepoSlug}";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
