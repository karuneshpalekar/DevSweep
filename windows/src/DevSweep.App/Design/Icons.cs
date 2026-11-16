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
}
