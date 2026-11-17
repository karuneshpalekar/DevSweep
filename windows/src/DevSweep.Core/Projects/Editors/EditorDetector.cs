using System.Diagnostics;
using System.IO.Enumeration;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Projects.Editors;

/// <summary>A code editor found on this PC.</summary>
/// <param name="Id">Stable name used in settings (<c>vscode</c>, <c>vscode-insiders</c>, <c>visualstudio</c>, <c>rider</c>, <c>cursor</c>, <c>windsurf</c>).</param>
/// <param name="Name">What the person sees ("Visual Studio Code").</param>
/// <param name="Path">The program's full path.</param>
public sealed record EditorInfo(string Id, string Name, string Path);

/// <summary>What the detector looks at (files, environment variables, the registry, vswhere); a fake layout in tests.</summary>
public interface IEditorEnvironment
{
    string? Variable(string name);

    bool FileExists(string path);

    /// <summary>Names of the folders directly inside <paramref name="parent"/>; empty when it does not exist.</summary>
    IReadOnlyList<string> SubFolders(string parent);

    /// <summary>The default value of <c>App Paths\&lt;exe&gt;</c> (HKCU first, then HKLM), or null.</summary>
    string? AppPath(string exe);

    /// <summary>The newest Visual Studio's <c>devenv.exe</c> according to vswhere, or null.</summary>
    string? VsWhere();
}

/// <summary>The real machine.</summary>
public sealed class WindowsEditorEnvironment(IRegistryReader? registry = null) : IEditorEnvironment
{
    public string? Variable(string name) => Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> SubFolders(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? [.. Directory.EnumerateDirectories(parent).Select(Path.GetFileName).OfType<string>()] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? AppPath(string exe)
    {
        foreach (RegistryHive hive in (RegistryHive[])[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            try
            {
                using IRegistryKeyReader? key = (registry ?? WindowsRegistryReader.Instance).Open(hive, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.Value(string.Empty) is string path && File.Exists(path.Trim('"')))
                {
                    return path.Trim('"');
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Try the next hive.
            }
        }

        return null;
    }

    public string? VsWhere()
    {
        string? x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (x86 is null)
        {
            return null;
        }

        string vswhere = Path.Join(x86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(vswhere)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (string a in (string[])["-latest", "-products", "*", "-property", "productPath"])
            {
                start.ArgumentList.Add(a);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }

            string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
            return line.Length > 0 && File.Exists(line) ? line : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Finds the code editors "Open in editor" can use (D3): VS Code, VS Code Insiders, Visual Studio (vswhere, else the
/// usual folders), Rider (including JetBrains Toolbox), Cursor and Windsurf. Looks at file locations, the registry's
/// App Paths and vswhere; never runs an editor.
/// </summary>
public static class EditorDetector
{
    private sealed record Candidate(string Id, string Name, string Exe, string[] Templates);

    private static readonly Candidate[] Candidates =
    [
        new("vscode", "Visual Studio Code", "Code.exe",
            [@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe", @"%ProgramFiles%\Microsoft VS Code\Code.exe", @"%ProgramFiles(x86)%\Microsoft VS Code\Code.exe"]),
        new("vscode-insiders", "VS Code Insiders", "Code - Insiders.exe",
            [@"%LOCALAPPDATA%\Programs\Microsoft VS Code Insiders\Code - Insiders.exe", @"%ProgramFiles%\Microsoft VS Code Insiders\Code - Insiders.exe"]),
        new("visualstudio", "Visual Studio", "devenv.exe",
            [@"%ProgramFiles%\Microsoft Visual Studio\*\*\Common7\IDE\devenv.exe", @"%ProgramFiles(x86)%\Microsoft Visual Studio\*\*\Common7\IDE\devenv.exe"]),
        new("rider", "JetBrains Rider", "rider64.exe",
            [@"%LOCALAPPDATA%\Programs\Rider\bin\rider64.exe", @"%ProgramFiles%\JetBrains\JetBrains Rider*\bin\rider64.exe", @"%LOCALAPPDATA%\JetBrains\Toolbox\apps\Rider\*\*\bin\rider64.exe", @"%LOCALAPPDATA%\JetBrains\Toolbox\apps\Rider\*\bin\rider64.exe"]),
        new("cursor", "Cursor", "Cursor.exe", [@"%LOCALAPPDATA%\Programs\cursor\Cursor.exe", @"%ProgramFiles%\Cursor\Cursor.exe"]),
        new("windsurf", "Windsurf", "Windsurf.exe", [@"%LOCALAPPDATA%\Programs\Windsurf\Windsurf.exe", @"%ProgramFiles%\Windsurf\Windsurf.exe"]),
    ];

    /// <summary>Every editor found, in a fixed order.</summary>
    public static IReadOnlyList<EditorInfo> Detect(IEditorEnvironment? environment = null)
    {
        environment ??= new WindowsEditorEnvironment();
        var found = new List<EditorInfo>();
        foreach (Candidate candidate in Candidates)
        {
            string? path = candidate.Id == "visualstudio" ? environment.VsWhere() : null;
            path ??= candidate.Templates.SelectMany(t => Expand(t, environment)).OrderDescending(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (path is null && candidate.Id is "vscode" or "vscode-insiders")
            {
                path = environment.AppPath(candidate.Exe);
            }

            if (path is not null)
            {
                found.Add(new EditorInfo(candidate.Id, candidate.Name, path));
            }
        }

        return found;
    }

    /// <summary>The editor to use: the chosen default when it is installed, else the first one found, else null (File Explorer is the fallback).</summary>
    public static EditorInfo? Choose(IReadOnlyList<EditorInfo> editors, string? defaultId)
    {
        ArgumentNullException.ThrowIfNull(editors);
        return editors.FirstOrDefault(e => e.Id.Equals(defaultId, StringComparison.OrdinalIgnoreCase)) ?? (editors.Count > 0 ? editors[0] : null);
    }

    /// <summary>The program and arguments that open <paramref name="folder"/> (an editor, else File Explorer); no shell is involved.</summary>
    public static (string Program, IReadOnlyList<string> Arguments) OpenCommand(EditorInfo? editor, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return editor is null
            ? (Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), [folder])
            : (editor.Path, [folder]);
    }

    public static void Open(EditorInfo? editor, string folder)
    {
        (string program, IReadOnlyList<string> arguments) = OpenCommand(editor, folder);
        var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start);
    }

    /// <summary>Expands <c>%VARS%</c> and <c>*</c> folder wildcards into the existing files that match.</summary>
    private static IEnumerable<string> Expand(string template, IEditorEnvironment environment)
    {
        string expanded = template;
        foreach (string name in (string[])["LOCALAPPDATA", "ProgramFiles(x86)", "ProgramFiles"])
        {
            string token = "%" + name + "%";
            if (expanded.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                if (environment.Variable(name) is not { Length: > 0 } value)
                {
                    return [];
                }

                expanded = expanded.Replace(token, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        string[] parts = expanded.Split('\\');
        IEnumerable<string> current = [parts[0] + "\\"];
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            bool last = i == parts.Length - 1;
            current = current.SelectMany(parent =>
            {
                if (last)
                {
                    string file = Join(parent, part);
                    return environment.FileExists(file) ? [file] : (IEnumerable<string>)[];
                }

                if (part.Contains('*', StringComparison.Ordinal))
                {
                    return environment.SubFolders(parent).Where(n => FileSystemName.MatchesSimpleExpression(part, n, ignoreCase: true)).Select(n => Join(parent, n));
                }

                return [Join(parent, part)];
            }).ToList();
        }

        return current;
    }

    private static string Join(string parent, string child) => parent.EndsWith('\\') ? parent + child : parent + "\\" + child;
}
