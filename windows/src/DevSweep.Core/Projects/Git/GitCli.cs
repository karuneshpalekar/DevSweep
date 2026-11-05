using System.Globalization;
using DevSweep.Core.Cleaning;

namespace DevSweep.Core.Projects.Git;

/// <summary>How one git call is made.</summary>
public sealed record GitCall
{
    public static GitCall ReadOnly { get; } = new();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Extra <c>-c key=value</c> settings for this call only (never written to any config file).</summary>
    public IReadOnlyList<string> Config { get; init; } = [];

    /// <summary>Extra variables for the child (a token for a credential helper, for example). Never logged.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// False (the default) for local checks: a partial clone may not download missing
    /// objects (<c>GIT_NO_LAZY_FETCH</c>), so a check never touches the network.
    /// </summary>
    public bool AllowNetwork { get; init; }

    /// <summary>Output kept; a check that gets truncated output reports "unknown", never a count.</summary>
    public int MaxOutputChars { get; init; } = 32 * 1024 * 1024;
}

/// <summary>What a git call did.</summary>
public sealed record GitResult(CommandResult Command)
{
    public bool Ok => Command.Succeeded && !Truncated;

    public string Output => Command.StandardOutput;

    public bool Truncated => Command.StandardOutput.EndsWith("[output truncated]", StringComparison.Ordinal);

    /// <summary>git's own words when it failed (first line), for "couldn't check" messages.</summary>
    public string Error
    {
        get
        {
            if (Command.StartError is { } start)
            {
                return start;
            }

            if (Command.TimedOut)
            {
                return string.Create(CultureInfo.InvariantCulture, $"git did not answer within {Command.Elapsed.TotalSeconds:0} seconds");
            }

            if (Truncated)
            {
                return "git's answer was too long to read completely";
            }

            string text = (Command.StandardError.Length > 0 ? Command.StandardError : Command.StandardOutput).Trim();
            string first = text.Split('\n', 2)[0].Trim();
            return first.Length > 0 ? first : string.Create(CultureInfo.InvariantCulture, $"git failed (exit {Command.ExitCode})");
        }
    }

    /// <summary>Output split at NUL (for <c>-z</c> output), empty entries dropped.</summary>
    public IReadOnlyList<string> NulSeparated() => Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Output lines, trimmed, empty ones dropped.</summary>
    public IReadOnlyList<string> Lines() => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Runs git. Faked in unit tests; the real one is <see cref="GitCli"/>.</summary>
public interface IGit
{
    /// <summary>The git program, or null when git is not installed.</summary>
    string? Program { get; }

    /// <summary><c>git -C &lt;directory&gt; &lt;arguments&gt;</c> (no <c>-C</c> when <paramref name="directory"/> is null).</summary>
    Task<GitResult> RunAsync(string? directory, IReadOnlyList<string> arguments, GitCall? settings = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// git.exe with no shell, a time limit and settings that keep a check read-only and
/// quiet: no prompts (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=never</c>), no
/// optional index writes (<c>GIT_OPTIONAL_LOCKS=0</c>), no file-system monitor program
/// (<c>core.fsmonitor=false</c>), long paths readable (<c>core.longpaths=true</c>), paths
/// unquoted, no colour. Variables inherited from a shell that would point git at another
/// repository (<c>GIT_DIR</c>, <c>GIT_WORK_TREE</c>...) are removed. Every setting is
/// passed with <c>-c</c> for this call only; no config file is ever changed.
/// </summary>
public sealed class GitCli(string? program) : IGit
{
    private static readonly string[] RepositoryVariables =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_ALTERNATE_OBJECT_DIRECTORIES"];

    /// <summary>The git found on this PC (<see cref="ToolLocator.Git"/>).</summary>
    public static GitCli Default { get; } = new(ToolLocator.Git());

    public string? Program { get; } = program;

    /// <summary>The argv for a call, without the program (also what the UI shows; tokens are never in it).</summary>
    public static IReadOnlyList<string> Arguments(string? directory, IReadOnlyList<string> arguments, GitCall call)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(call);
        var argv = new List<string>
        {
            "-c", "core.fsmonitor=false",
            "-c", "core.longpaths=true",
            "-c", "core.quotepath=false",
            "-c", "color.ui=false",
        };
        foreach (string setting in call.Config)
        {
            argv.Add("-c");
            argv.Add(setting);
        }

        if (directory is not null)
        {
            argv.Add("-C");
            argv.Add(directory);
        }

        argv.AddRange(arguments);
        return argv;
    }

    public async Task<GitResult> RunAsync(string? directory, IReadOnlyList<string> arguments, GitCall? settings = null, CancellationToken cancellationToken = default)
    {
        GitCall call = settings ?? GitCall.ReadOnly;
        if (Program is null)
        {
            return new GitResult(new CommandResult(-1, string.Empty, string.Empty, false, TimeSpan.Zero, "git is not installed (Git for Windows: winget install Git.Git)"));
        }

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_PAGER"] = "cat",
        };
        foreach (string name in RepositoryVariables)
        {
            environment[name] = null;
        }

        if (!call.AllowNetwork)
        {
            environment["GIT_NO_LAZY_FETCH"] = "1";
        }

        foreach ((string name, string? value) in call.Environment)
        {
            environment[name] = value;
        }

        var options = new CommandOptions
        {
            Environment = environment,
            MaxCapturedChars = call.MaxOutputChars,
            WorkingDirectory = directory is not null && Directory.Exists(directory) ? directory : Path.GetTempPath(),
        };
        CommandResult result = await CommandRunner.RunAsync([Program, .. Arguments(directory, arguments, call)], call.Timeout, options, cancellationToken)
            .ConfigureAwait(false);
        return new GitResult(result);
    }
}
