using System.Security.Principal;

namespace DevSweep.Fixtures;

/// <summary>Whether the test process runs with an administrator token.</summary>
public static class Elevation
{
    public static bool IsAdministrator { get; } = Check();

    /// <summary>"administrator" or "standard user", for test logs.</summary>
    public static string Describe() => IsAdministrator ? "administrator" : "standard user";

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
