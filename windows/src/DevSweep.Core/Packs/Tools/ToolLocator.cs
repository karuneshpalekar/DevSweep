using DevSweep.Core.Locations;

namespace DevSweep.Core.Packs.Tools;

/// <summary>
/// Finds a tool's program the way Windows would run it: the <c>PATH</c> folders in
/// order, then the tool's usual install folders. Only <c>.exe</c> files: <c>.cmd</c> and
/// <c>.ps1</c> shims need a shell, which DevSweep never uses.
/// </summary>
public static class ToolLocator
{
    /// <summary>The full path of <paramref name="exe"/> (e.g. <c>docker.exe</c>), or null.</summary>
    public static string? Find(ISystemEnvironment environment, string exe, params string?[] fallbacks)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(exe);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .exe programs can be run without a shell.", nameof(exe));
        }

        foreach (string folder in (environment.GetVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed = folder.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue; // A relative PATH entry depends on the current folder: never used.
            }

            string candidate = Path.Join(trimmed, exe);
            if (environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        return fallbacks.FirstOrDefault(f => f is not null && Path.IsPathFullyQualified(f) && environment.FileExists(f));
    }
}
