namespace DevSweep.Core.Health.Common;

/// <summary>Builds PowerShell command text safely: every value is a single-quoted literal (no expansion, no injection).</summary>
public static class PowerShellText
{
    /// <summary><c>'text'</c> with embedded quotes doubled (PowerShell's literal string).</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "'" + text.Replace("'", "''", StringComparison.Ordinal)
            .Replace("‘", "‘‘", StringComparison.Ordinal)
            .Replace("’", "’’", StringComparison.Ordinal) + "'";
    }

    /// <summary>A command that sends a folder to the Recycle Bin (restorable), the way Explorer does.</summary>
    public static string RecycleFolder(string path)
        => $"Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory({Quote(path)}, 'OnlyErrorDialogs', 'SendToRecycleBin')";

    /// <summary>Runs a program by full path with literal arguments (the call operator, no re-parsing).</summary>
    public static string Run(string program, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return "& " + Quote(program) + string.Concat(arguments.Select(a => " " + Quote(a)));
    }
}
