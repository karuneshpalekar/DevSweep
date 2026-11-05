using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Health.Security;

/// <summary>Asks git about a file. Faked in tests.</summary>
public interface IGitRunner
{
    /// <summary>git's exit code for <c>git -C &lt;directory&gt; &lt;arguments&gt;</c>, or null when it could not be run.</summary>
    int? ExitCode(string directory, params string[] arguments);
}

/// <summary>
/// Runs <c>git.exe</c> (found on the Explorer PATH, Scoop shims followed, and trusted: a
/// plain local program outside Downloads/Desktop/Documents/Temp) with no shell, a
/// 10-second limit, and settings that stop a repository from running its own programs
/// (<c>core.fsmonitor=false</c>, hooks off): the folders scanned may hold downloaded repositories.
/// </summary>
public sealed class WindowsGitRunner : IGitRunner
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private WindowsGitRunner(string git) => Git = git;

    public string Git { get; }

    /// <summary>A runner for the git a new terminal would run, or null with the reason.</summary>
    public static WindowsGitRunner? Find(ProbeContext context, out string? whyNot)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = new List<string>();
        foreach (PathHit hit in context.Path.Find("git.exe", context.Aliases).Where(h => h.Alias is null))
        {
            candidates.Add(ShimResolver.Resolve(hit, context.Environment).Runs);
        }

        candidates.AddRange(context.ProgramFilesFolders.Select(pf => Path.Join(pf, "Git", "cmd", "git.exe")));
        whyNot = "git.exe was not found on PATH or in Program Files";
        foreach (string candidate in candidates)
        {
            string? reason = TrustedPrograms.WhyNot(candidate, context.Environment);
            if (reason is null)
            {
                whyNot = null;
                return new WindowsGitRunner(candidate);
            }

            whyNot = $"{candidate} was not used: {reason}";
        }

        return null;
    }

    public int? ExitCode(string directory, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] argv = [Git, "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-C", directory, .. arguments];
        CommandResult result = CommandRunner.RunAsync(argv, Limit).GetAwaiter().GetResult();
        return result.StartError is null && !result.TimedOut ? result.ExitCode : null;
    }
}

/// <summary>Fixed answers (tests): exit codes per "directory|arguments".</summary>
public sealed class FixedGitRunner : IGitRunner
{
    public Dictionary<string, int> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int? ExitCode(string directory, params string[] arguments)
        => Answers.TryGetValue(directory + "|" + string.Join(' ', arguments), out int code) ? code : null;
}
