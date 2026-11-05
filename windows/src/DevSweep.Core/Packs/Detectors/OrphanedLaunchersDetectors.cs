using System.Text.RegularExpressions;
using System.Xml;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Rules;
using Microsoft.Win32;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>Which program a command line starts, and whether it is certainly gone.</summary>
public static partial class CommandLines
{
    /// <summary>
    /// The program's full path: the quoted first part, or everything up to the first <c>.exe</c>
    /// (unquoted paths with spaces), or the first word. <c>%VARS%</c> are expanded; null when a
    /// variable is unknown or the result is not a full local path (then nobody can tell it is gone).
    /// </summary>
    public static string? ProgramPath(string commandLine, ISystemEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(environment);
        string text = commandLine.Trim();
        string program;
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            program = text[1..close];
        }
        else if (text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is int exe and > 0)
        {
            program = text[..(exe + 4)];
        }
        else
        {
            program = text.Split(' ', 2)[0];
        }

        bool unknown = false;
        string expanded = Variable().Replace(program, m =>
        {
            string? value = environment.GetVariable(m.Groups[1].Value);
            unknown |= value is null;
            return value ?? m.Value;
        });
        return unknown || expanded.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(expanded) || Path.GetExtension(expanded).Length == 0
            ? null
            : expanded;
    }

    /// <summary>True only when the program's drive is there and the file is not.</summary>
    public static bool IsMissing(string program, ISystemEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(environment);
        string? root = Path.GetPathRoot(program);
        return root is { Length: > 0 } && environment.DirectoryExists(root) && !environment.FileExists(program)
            && FileProbe.Stat(program) is null;
    }

    [GeneratedRegex("%([^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}

/// <summary>
/// <c>kind: orphanedScheduledTasks</c>. Scheduled tasks whose every program is gone (an app was
/// removed and left its task behind). Tasks under <c>\Microsoft\</c> are never looked at. Read
/// from the task files (<c>{Windows}\System32\Tasks</c>); where those may not be read (standard
/// users), from <c>schtasks /query /xml ONE</c> (30 s limit), which lists the tasks this account
/// can see. Shown with <c>schtasks /delete</c>; DevSweep changes nothing.
/// </summary>
public sealed class OrphanedScheduledTasksDetector(IMachineQueries machine) : Detector<OrphanedScheduledTasksDetector.Options>
{
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public override string Kind => "orphanedScheduledTasks";

    /// <summary>Each task's name and its Exec commands, from one task file or from <c>schtasks /query /xml ONE</c> (names in comments).</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Commands)>? Parse(string xml, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var tasks = new List<(string, IReadOnlyList<string>)>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
            string? current = name;
            List<string>? commands = null;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Comment && reader.Value.Trim().StartsWith('\\'))
                {
                    current = reader.Value.Trim();
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Task" && reader.NamespaceURI == TaskNamespace)
                {
                    commands = [];
                    if (reader.IsEmptyElement)
                    {
                        tasks.Add((current ?? "?", commands));
                        commands = null;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Command" && commands is not null)
                {
                    commands.Add(reader.ReadElementContentAsString().Trim());
                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Task")
                    {
                        tasks.Add((current ?? "?", commands));
                        commands = null;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Task" && commands is not null)
                {
                    tasks.Add((current ?? "?", commands));
                    commands = null;
                }
            }

            return tasks;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(options.Paths("tasks", required: true));
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var env = run.Context.Locations.Environment;
        List<(string Name, IReadOnlyList<string> Commands)>? tasks = null;
        string source = string.Empty;
        foreach (PathTemplate template in options.Tasks)
        {
            run.Expand(template);
            string? folder = run.Context.Locations.Expand(template, out _);
            if (folder is not null && FromFiles(run, folder) is { } fromFiles)
            {
                tasks = fromFiles;
                source = $"the task files in {folder}";
                break;
            }
        }

        if (tasks is null)
        {
            string? schtasks = run.Context.Locations.Resolve("Windows").Path is { } windows ? Path.Join(windows, "System32", "schtasks.exe") : null;
            if (schtasks is null || !env.FileExists(schtasks))
            {
                run.Note("Scheduled tasks could not be read (no task files and no schtasks.exe)");
                return [];
            }

            CommandResult result = machine.Run([schtasks, "/query", "/xml", "ONE"], TimeSpan.FromSeconds(30), run.CancellationToken);
            if (!result.Succeeded || Parse(result.StandardOutput) is not { } parsed)
            {
                run.Note($"schtasks /query gave no answer: {(result.StartError ?? result.StandardError).Trim()}");
                return [];
            }

            tasks = parsed.ToList();
            source = "schtasks /query (the tasks this account can see)";
        }

        var candidates = new List<Candidate>();
        foreach ((string name, IReadOnlyList<string> commands) in tasks)
        {
            if (name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || commands.Count == 0)
            {
                continue;
            }

            List<string?> programs = commands.Select(c => CommandLines.ProgramPath(c, env)).ToList();
            if (programs.Any(p => p is null) || !programs.All(p => CommandLines.IsMissing(p!, env)))
            {
                continue;
            }

            string missing = Words.Join(programs.Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            candidates.Add(new Candidate
            {
                Key = name,
                Size = 0,
                Title = $"Scheduled task {name}",
                Subtitle = missing,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = name, ["program"] = missing },
                Checks =
                [
                    Check.Passed($"Read from {source}"),
                    Check.Passed($"Its program is gone: {missing} does not exist, and its drive does"),
                    Check.Passed("Not a Windows task (nothing under \\Microsoft\\ is looked at)"),
                ],
            });
        }

        return candidates;
    }

    private static List<(string, IReadOnlyList<string>)>? FromFiles(DetectorRun run, string folder)
    {
        var tasks = new List<(string, IReadOnlyList<string>)>();
        var pending = new Stack<(string Path, string Name)>();
        pending.Push((folder, string.Empty));
        while (pending.Count > 0)
        {
            (string path, string name) = pending.Pop();
            ProbeListing listing = FileProbe.List(path);
            if (listing.Error != ProbeError.None)
            {
                return null; // Not readable as this user (or missing): use schtasks instead.
            }

            foreach (ProbeEntry entry in listing.Entries)
            {
                string taskName = $"{name}\\{entry.Name}";
                if (entry.CanEnter)
                {
                    if (!(name.Length == 0 && entry.Name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)))
                    {
                        pending.Push((entry.Path, taskName));
                    }
                }
                else if (!entry.IsDirectory && run.Context.Locations.Environment.ReadAllText(entry.Path) is { } xml && Parse(xml, taskName) is { } parsed)
                {
                    tasks.AddRange(parsed.Select(t => (taskName, t.Commands)));
                }
            }
        }

        return tasks;
    }

    /// <summary>Bound options of an <c>orphanedScheduledTasks</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> Tasks);
}

/// <summary>
/// <c>kind: orphanedRunEntries</c>. Programs set to start at sign-in (<c>HKCU\...\Run</c>) whose
/// program file is gone. Only full local paths are judged. Shown with <c>reg delete</c>; DevSweep
/// changes nothing.
/// </summary>
public sealed class OrphanedRunEntriesDetector : Detector<OrphanedRunEntriesDetector.Options>
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public override string Kind => "orphanedRunEntries";

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options();
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        var env = run.Context.Locations.Environment;
        var candidates = new List<Candidate>();
        try
        {
            using IRegistryKeyReader? key = run.Context.Registry.Open(RegistryHive.CurrentUser, RegistryView.Registry64, RunKey);
            if (key is null)
            {
                return [];
            }

            foreach (string name in key.ValueNames())
            {
                if (key.Value(name) is not string command || CommandLines.ProgramPath(command, env) is not { } program || !CommandLines.IsMissing(program, env))
                {
                    continue;
                }

                candidates.Add(new Candidate
                {
                    Key = name,
                    Size = 0,
                    Title = $"Startup entry “{name}”",
                    Subtitle = program,
                    Facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name, ["program"] = program, ["command"] = command },
                    Checks =
                    [
                        Check.Passed($"HKCU\\{RunKey} starts {command} at sign-in"),
                        Check.Passed($"Its program is gone: {program} does not exist, and its drive does"),
                    ],
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            run.Note($"HKCU\\{RunKey} could not be read");
        }

        return candidates;
    }

    /// <summary>Bound options of an <c>orphanedRunEntries</c> rule (none).</summary>
    public sealed record Options;
}
