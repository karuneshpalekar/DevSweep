using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;

namespace DevSweep.Core.Projects.Actions;

/// <summary>
/// Per-command GitHub credentials for one account, without touching any config file or the
/// active gh account: <c>-c credential.helper=</c> (clears every helper from the system and
/// global config for this call), then <c>-c credential.helper=!'&lt;gh.exe&gt;' auth git-credential</c>
/// (the form <c>gh auth setup-git</c> writes), with that account's token in <c>GH_TOKEN</c>
/// in git's environment only. gh's helper answers with <c>GH_TOKEN</c>, so git authenticates as
/// that account. The token is never on a command line, in a file or in a log.
/// </summary>
public static class GitCredentials
{
    /// <summary>The <c>-c</c> settings (no secrets in them, safe to show).</summary>
    public static IReadOnlyList<string> Config(string ghProgram)
    {
        ArgumentNullException.ThrowIfNull(ghProgram);
        string path = ghProgram.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
        return ["credential.helper=", $"credential.helper=!'{path}' auth git-credential", "credential.useHttpPath=false"];
    }

    /// <summary>A network git call (clone, fetch, push) authenticated as <paramref name="token"/>'s account.</summary>
    public static GitCall For(string? ghProgram, GitHubToken? token, TimeSpan timeout)
    {
        if (ghProgram is null || token is null)
        {
            return new GitCall { AllowNetwork = true, Timeout = timeout };
        }

        var environment = new Dictionary<string, string?>(GitHubCli.QuietEnvironment) { ["GH_TOKEN"] = token.Value };
        return new GitCall { AllowNetwork = true, Timeout = timeout, Config = Config(ghProgram), Environment = environment };
    }
}
