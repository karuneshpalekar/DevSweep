namespace DevSweep.App.Design;

/// <summary>
/// The one glyph map (Segoe Fluent Icons code points). The same action has the same icon on every screen; views use
/// these names, never a literal code point.
/// </summary>
internal static class Icons
{
    public const string Home = "";
    public const string CleanUp = "";
    public const string Projects = "";
    public const string Health = "";
    public const string History = "";
    public const string Settings = "";

    /// <summary>The navigation glyph for a place; <paramref name="fallback"/> when the id is not known.</summary>
    public static string ForPlace(string id, string fallback) => id switch
    {
        "home" => Home,
        "cleanup" => CleanUp,
        "projects" => Projects,
        "health" => Health,
        "history" => History,
        "settings" => Settings,
        _ => fallback,
    };

    public const string Info = "";
    public const string Close = "";
    public const string Search = "";
    public const string Refresh = "";
    public const string Check = "";
    public const string CheckCircle = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Lock = "";
    public const string Download = "";
    public const string Folder = "";
    public const string OpenFolder = "";
    public const string Copy = "";
    public const string Delete = "";
    public const string Restore = "";
    public const string ChevronDown = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Auto = "";
    public const string Logo = "";
    public const string Filter = "";
    public const string Shield = "";
    public const string Update = "";
    public const string Add = "\uE710";
    public const string Code = "\uE943";
    public const string OpenInNew = "\uE8A7";
    public const string Upload = "\uE898";
    public const string Person = "\uE77B";
    public const string PersonAdd = "\uE8FA";
    public const string Clock = "\uE823";
    public const string Edit = "\uE70F";
    public const string Tag = "\uE8EC";
    public const string Wsl = "\uE756";

    // Health
    public const string Terminal = "\uE756";
    public const string Globe = "\uE774";
    public const string Network = "\uE968";
    public const string Key = "\uE192";
    public const string Cloud = "\uE753";
    public const string Page = "\uE7C3";
    public const string Package = "\uE7B8";
    public const string Stop = "\uE71A";
    public const string Link = "\uE71B";
    public const string Up = "\uE74A";
    public const string Certificate = "\uEB95";
    public const string Contact = "\uE77B";
}
