using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// Runs a program to ask its version, as a last resort (files are read first). Faked in tests.
/// </summary>
public interface IVersionCommand
{
    /// <summary>Standard output and error, or null when it was not run (untrusted, failed, timed out).</summary>
    string? Run(string program, IReadOnlyList<string> arguments);
}

/// <summary>Never runs anything (tests, and offline/fast modes).</summary>
public sealed class NoVersionCommand : IVersionCommand
{
    public static NoVersionCommand Instance { get; } = new();

    public string? Run(string program, IReadOnlyList<string> arguments) => null;
}

/// <summary>Canned answers per program path (tests).</summary>
public sealed class FixedVersionCommand : IVersionCommand
{
    public Dictionary<string, string> Outputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Ran { get; } = [];

    public string? Run(string program, IReadOnlyList<string> arguments)
    {
        Ran.Add(program);
        return Outputs.TryGetValue(program, out string? output) ? output : null;
    }
}

/// <summary>
/// Decides whether a program may be run at all: a full path to a plain local
/// <c>.exe</c> (no link, no cloud placeholder, no script that needs a shell), not
/// in a folder where downloads and documents land (Downloads, Desktop, Documents, Temp).
/// </summary>
public static class TrustedPrograms
{
    public static string? WhyNot(string program, ISystemEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(environment);
        if (!Path.IsPathFullyQualified(program))
        {
            return "not a full path";
        }

        if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "not a program (.exe)";
        }

        if (!HealthFiles.IsFile(program))
        {
            return "missing, a link or a cloud-only file";
        }

        string full = Path.GetFullPath(program);
        foreach (string? root in (string?[])[environment.KnownFolder("Downloads"), environment.KnownFolder("Desktop"), environment.KnownFolder("Documents"), environment.TempPath])
        {
            if (root is not null && full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return $"in {root}, where downloaded files land";
            }
        }

        return null;
    }
}

/// <summary>Runs trusted programs with no shell, a 10-second limit and captured output.</summary>
public sealed class TrustedVersionCommand(ISystemEnvironment environment) : IVersionCommand
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);

    public string? Run(string program, IReadOnlyList<string> arguments)
    {
        if (TrustedPrograms.WhyNot(program, environment) is not null)
        {
            return null;
        }

        CommandResult result = CommandRunner.RunAsync([program, .. arguments], Timeout).GetAwaiter().GetResult();
        return result.StartError is null && !result.TimedOut ? result.StandardOutput + "\n" + result.StandardError : null;
    }
}
