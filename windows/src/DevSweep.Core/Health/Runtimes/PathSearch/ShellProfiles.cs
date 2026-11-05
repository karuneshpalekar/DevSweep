using System.Globalization;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>A shell start-up file line that changes PATH or activates a version manager.</summary>
/// <param name="Shell">"PowerShell 7", "Windows PowerShell", "Git Bash", "Command Prompt".</param>
/// <param name="File">The file (or registry value) it is in.</param>
/// <param name="Line">1-based line number (0 for a registry value).</param>
/// <param name="What">What it does, from a fixed list ("runs fnm env", "changes PATH"); never the line's text, which may hold secrets.</param>
/// <param name="Tools">Tool ids it affects; empty means any.</param>
public sealed record ShellNote(string Shell, string File, int Line, string What, IReadOnlyList<string> Tools)
{
    public string Describe()
        => Line > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Shell}: {Path.GetFileName(File)} line {Line} {What}")
            : $"{Shell}: {File} {What}";
}

/// <summary>
/// Reads (never runs) the start-up files of PowerShell, Windows PowerShell, Git Bash
/// and the Command Prompt's AutoRun, and notes lines that change which version a
/// command finds, so Health can say "may differ in PowerShell" instead of guessing.
/// </summary>
public static class ShellProfiles
{
    private static readonly (string Needle, string What, string[] Tools)[] Patterns =
    [
        ("fnm env", "runs fnm env", ["node"]),
        ("fnm use", "runs fnm use", ["node"]),
        ("nvm use", "runs nvm use", ["node"]),
        ("volta", "sets up Volta", ["node"]),
        ("pyenv", "sets up pyenv", ["python"]),
        ("conda initialize", "initialises conda", ["python"]),
        ("conda.exe", "initialises conda", ["python"]),
        ("conda activate", "activates a conda environment", ["python"]),
        ("mise activate", "activates mise", []),
        ("asdf", "sets up asdf", []),
        ("rbenv", "sets up rbenv", ["ruby"]),
        ("ridk", "sets up RubyInstaller's MSYS2", ["ruby"]),
        ("JAVA_HOME", "sets JAVA_HOME", ["java"]),
        ("GOROOT", "sets GOROOT", ["go"]),
        ("$env:path", "changes PATH", []),
        ("$env:PATH", "changes PATH", []),
        ("export PATH", "changes PATH", []),
        ("PATH=", "changes PATH", []),
        ("set path", "changes PATH", []),
    ];

    public static IReadOnlyList<ShellNote> Read(ISystemEnvironment environment, IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(registry);
        var notes = new List<ShellNote>();
        string? documents = environment.KnownFolder("Documents");
        string? profile = environment.KnownFolder("Profile");
        var files = new List<(string Shell, string? Path)>
        {
            ("PowerShell 7", documents is null ? null : Path.Join(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1")),
            ("PowerShell 7", documents is null ? null : Path.Join(documents, "PowerShell", "profile.ps1")),
            ("Windows PowerShell", documents is null ? null : Path.Join(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")),
            ("Windows PowerShell", documents is null ? null : Path.Join(documents, "WindowsPowerShell", "profile.ps1")),
            ("Git Bash", profile is null ? null : Path.Join(profile, ".bashrc")),
            ("Git Bash", profile is null ? null : Path.Join(profile, ".bash_profile")),
            ("Git Bash", profile is null ? null : Path.Join(profile, ".profile")),
        };
        foreach ((string shell, string? path) in files)
        {
            if (path is not null && HealthFiles.ReadText(path, 256 * 1024) is { } text)
            {
                notes.AddRange(Scan(shell, path, text));
            }
        }

        foreach (RegistryHive hive in (RegistryHive[])[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            try
            {
                using IRegistryKeyReader? key = registry.Open(hive, RegistryView.Registry64, @"Software\Microsoft\Command Processor");
                if (key?.Value("AutoRun") is string autoRun && autoRun.Trim().Length > 0)
                {
                    notes.Add(new ShellNote("Command Prompt", $"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")} AutoRun", 0, "runs a command at start-up", []));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Not readable: say nothing rather than guess.
            }
        }

        return notes;
    }

    /// <summary>The notes for one file's text.</summary>
    public static IReadOnlyList<ShellNote> Scan(string shell, string file, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var notes = new List<ShellNote>();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            bool comment = line.StartsWith('#') || line.StartsWith("::", StringComparison.Ordinal) || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase);
            foreach ((string needle, string what, string[] tools) in Patterns)
            {
                bool marker = needle == "conda initialize";
                if ((!comment || marker) && line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    if (!notes.Any(n => n.Line == i + 1 && n.What == what))
                    {
                        notes.Add(new ShellNote(shell, file, i + 1, what, tools));
                    }

                    break;
                }
            }
        }

        return notes;
    }

    /// <summary>The shells whose start-up files may change what <paramref name="toolId"/> resolves to.</summary>
    public static IReadOnlyList<string> MayDiffer(IReadOnlyList<ShellNote> notes, string toolId)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return notes.Where(n => n.Tools.Count == 0 || n.Tools.Contains(toolId, StringComparer.OrdinalIgnoreCase))
            .Select(n => n.Describe()).Distinct(StringComparer.Ordinal).ToList();
    }
}
