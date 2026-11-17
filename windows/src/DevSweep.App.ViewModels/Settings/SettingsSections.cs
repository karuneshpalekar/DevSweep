namespace DevSweep.App.ViewModels.Settings;

/// <summary>One section of the Settings page: the id the views and tests use, the group chip it belongs to, its title and search words.</summary>
public sealed record SettingsSection(string Id, string Group, string Title, string Keywords);

/// <summary>
/// The sections of Settings (the Mac's one scrolling page, in Windows wording) and the "Search settings" filter. A row of chips
/// (one per group) jumps to a section in one click; typing in the search box shows only the sections whose title or words match.
/// </summary>
public static class SettingsSections
{
    /// <summary>The group chips, in order: id and label.</summary>
    public static IReadOnlyList<(string Id, string Title)> Groups { get; } =
    [
        ("general", "General"),
        ("background", "Background"),
        ("alerts", "Alerts"),
        ("github", "GitHub"),
        ("folders", "Folders"),
        ("privacy", "Privacy"),
        ("modules", "Modules"),
        ("about", "About"),
    ];

    public static IReadOnlyList<SettingsSection> All { get; } =
    [
        new("appearance", "general", "Appearance", "theme light dark system mode colour color"),
        new("updates", "general", "Updates", "update version release check github new"),
        new("login", "background", "Open at login", "startup tray start sign in windows"),
        new("schedule", "background", "Scheduled scans", "schedule daily weekly time day scan automatic"),
        new("alerts", "alerts", "Alert me when", "notification toast disk full growth end of life secret reminder test focus"),
        new("accounts", "github", "GitHub accounts", "git identity name email commit sign in account add refresh"),
        new("downloads", "github", "Downloads", "clone download folder workspace code"),
        new("folders", "folders", "Project folders", "search idle node_modules add remove restore defaults drive"),
        new("editor", "folders", "Default editor", "open in editor visual studio code rider cursor windsurf"),
        new("ignored", "folders", "Ignored items", "ignore stop ignoring hidden left out"),
        new("scanning", "folders", "Scanning", "exact sizes overview timeout slow check alternate data streams"),
        new("permissions", "privacy", "Permissions and privacy", "controlled folder access windows security ransomware blocked network endoflife privacy reads"),
        new("troubleshooting", "privacy", "Troubleshooting", "report copy log diagnostics timings problem help support"),
        new("modules", "modules", "Modules", "feature flags health projects clean up turn off"),
        new("about", "about", "About", "version unsigned smartscreen data folder history quarantine"),
    ];

    /// <summary>The sections whose title, group or search words contain every word typed (all of them when nothing is typed).</summary>
    public static IReadOnlyList<SettingsSection> Matching(string? query)
    {
        string[] words = (query ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return All;
        }

        return [.. All.Where(section =>
        {
            string haystack = section.Title + " " + section.Group + " " + section.Keywords;
            return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        })];
    }

    public static string GroupOf(string sectionId) => All.FirstOrDefault(s => s.Id == sectionId)?.Group ?? "general";
}
