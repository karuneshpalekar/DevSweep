using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Rules.Yaml;

namespace DevSweep.Core.Projects.GitHub;

/// <summary>Whether GitHub features can work, and why not.</summary>
public enum GitHubStatus
{
    /// <summary>The GitHub CLI (gh) is not installed.</summary>
    NotInstalled,

    /// <summary>gh is installed but no account is signed in.</summary>
    NotSignedIn,

    SignedIn,
}

/// <summary>The accounts the GitHub CLI knows.</summary>
public sealed record GitHubAccounts(GitHubStatus Status, IReadOnlyList<GitHubAccount> Accounts, string? Message = null)
{
    public GitHubAccount? Active => Accounts.FirstOrDefault(a => a.IsActive);
}

/// <summary>The GitHub CLI: accounts and per-account tokens. Faked in tests.</summary>
public interface IGitHubCli
{
    /// <summary>gh.exe, or null when it is not installed.</summary>
    string? Program { get; }

    Task<GitHubAccounts> AccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>The token of one account (<c>gh auth token --user</c>), or null with the reason in <paramref name="error"/>.</summary>
    Task<(GitHubToken? Token, string? Error)> TokenAsync(string login, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the GitHub CLI's accounts without ever changing the active one: <c>gh auth status</c>
/// (every account on github.com and which is active), merged with <c>hosts.yml</c> in
/// <c>%GH_CONFIG_DIR%</c> or <c>%APPDATA%\GitHub CLI</c> (so accounts are listed even when gh
/// cannot run). Tokens come from <c>gh auth token --hostname github.com --user &lt;login&gt;</c>, one
/// account at a time. gh runs with no shell, a 30-second limit, no prompts and with
/// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> removed from its environment (with one of those set, gh
/// would report that token instead of the account asked for). Tokens are never logged.
/// </summary>
public sealed partial class GitHubCli(string? program, string? configDirectory = null) : IGitHubCli
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static GitHubCli Default { get; } = new(ToolLocator.GitHubCli());

    public string? Program { get; } = program;

    /// <summary><c>%GH_CONFIG_DIR%</c>, else <c>%APPDATA%\GitHub CLI</c>.</summary>
    public static string? DefaultConfigDirectory
        => Environment.GetEnvironmentVariable("GH_CONFIG_DIR") is { Length: > 0 } dir ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData ? Path.Join(appData, "GitHub CLI")
            : null;

    /// <summary>The environment every gh call gets (on top of this process's).</summary>
    public static IReadOnlyDictionary<string, string?> QuietEnvironment { get; } = new Dictionary<string, string?>
    {
        ["GH_TOKEN"] = null,
        ["GITHUB_TOKEN"] = null,
        ["GH_ENTERPRISE_TOKEN"] = null,
        ["GITHUB_ENTERPRISE_TOKEN"] = null,
        ["GH_HOST"] = RemoteUrls.GitHubHost,
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["NO_COLOR"] = "1",
        ["CLICOLOR"] = "0",
    };

    public async Task<GitHubAccounts> AccountsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitHubAccount> fromFile = ReadHostsFile(configDirectory ?? DefaultConfigDirectory);
        if (Program is null)
        {
            return new GitHubAccounts(GitHubStatus.NotInstalled, fromFile, "The GitHub CLI isn't installed. Install it with: winget install GitHub.cli");
        }

        CommandResult status = await RunAsync(["auth", "status", "--hostname", RemoteUrls.GitHubHost], cancellationToken).ConfigureAwait(false);
        if (status.StartError is not null || status.TimedOut)
        {
            return new GitHubAccounts(fromFile.Count > 0 ? GitHubStatus.SignedIn : GitHubStatus.NotSignedIn, fromFile,
                "gh auth status did not answer: " + (status.StartError ?? "timed out"));
        }

        List<GitHubAccount> accounts = ParseAuthStatus(status.StandardOutput + "\n" + status.StandardError).ToList();
        foreach (GitHubAccount account in fromFile)
        {
            if (!accounts.Any(a => a.Login.Equals(account.Login, StringComparison.OrdinalIgnoreCase)))
            {
                accounts.Add(account with { IsActive = accounts.Count == 0 && account.IsActive });
            }
        }

        return accounts.Count == 0
            ? new GitHubAccounts(GitHubStatus.NotSignedIn, [], "You aren't signed in to GitHub. Use Sign in (it runs gh auth login in a window you can see).")
            : new GitHubAccounts(GitHubStatus.SignedIn, accounts);
    }

    public async Task<(GitHubToken? Token, string? Error)> TokenAsync(string login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (!IsLogin(login))
        {
            return (null, $"\"{login}\" isn't a GitHub user name.");
        }

        if (Program is null)
        {
            return (null, "The GitHub CLI isn't installed. Install it with: winget install GitHub.cli");
        }

        CommandResult result = await RunAsync(["auth", "token", "--hostname", RemoteUrls.GitHubHost, "--user", login], cancellationToken).ConfigureAwait(false);
        string token = result.StandardOutput.Trim();
        if (!result.Succeeded || token.Length == 0 || token.Contains('\n', StringComparison.Ordinal))
        {
            // The error text is gh's own; it never contains a token.
            string why = result.StartError ?? (result.TimedOut ? "gh auth token timed out" : result.StandardError.Trim());
            return (null, $"No token for {login}: {(why.Length > 0 ? why : "gh gave none")}. Sign in again with gh auth login.");
        }

        return (new GitHubToken(login, token), null);
    }

    /// <summary>Accounts in <c>gh auth status</c> output (the Mac <c>parseAccounts</c>).</summary>
    public static IReadOnlyList<GitHubAccount> ParseAuthStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var accounts = new List<GitHubAccount>();
        foreach (string line in text.Split('\n'))
        {
            Match m = LoggedIn().Match(line);
            if (m.Success)
            {
                if (!accounts.Any(a => a.Login == m.Groups["login"].Value))
                {
                    accounts.Add(new GitHubAccount(m.Groups["login"].Value, false, m.Groups["host"].Value));
                }
            }
            else if (line.Contains("Active account: true", StringComparison.Ordinal) && accounts.Count > 0)
            {
                accounts[^1] = accounts[^1] with { IsActive = true };
            }
        }

        return accounts;
    }

    /// <summary>
    /// Accounts in hosts.yml: gh 2.40+ lists them under <c>github.com: users:</c> with the active one
    /// in <c>user:</c>; older files only have <c>user:</c>. Token values in the file are never read out.
    /// </summary>
    public static IReadOnlyList<GitHubAccount> ParseHostsFile(string text)
    {
        var accounts = new List<GitHubAccount>();
        YamlNode? root;
        try
        {
            root = YamlReader.Parse(text);
        }
        catch (Exception ex) when (ex is Rules.RuleSyntaxException or FormatException or InvalidOperationException)
        {
            return accounts;
        }

        if (root is not YamlMapping hosts || hosts.Get(RemoteUrls.GitHubHost) is not YamlMapping host)
        {
            return accounts;
        }

        string? active = (host.Get("user") as YamlScalar)?.Value;
        if (host.Get("users") is YamlMapping users)
        {
            foreach ((YamlScalar key, _) in users.Entries)
            {
                accounts.Add(new GitHubAccount(key.Value, key.Value == active, RemoteUrls.GitHubHost, "hosts.yml"));
            }
        }

        if (active is { Length: > 0 } && !accounts.Any(a => a.Login == active))
        {
            accounts.Add(new GitHubAccount(active, true, RemoteUrls.GitHubHost, "hosts.yml"));
        }

        return accounts.Where(a => IsLogin(a.Login)).ToList();
    }

    /// <summary>A GitHub user or organisation name (letters, digits, single hyphens), so it can never be read as an option.</summary>
    public static bool IsLogin(string login) => LoginPattern().IsMatch(login);

    private static IReadOnlyList<GitHubAccount> ReadHostsFile(string? directory)
    {
        if (directory is null)
        {
            return [];
        }

        try
        {
            string file = Path.Join(directory, "hosts.yml");
            return File.Exists(file) ? ParseHostsFile(File.ReadAllText(file)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => CommandRunner.RunAsync([Program!, .. arguments], Limit, new CommandOptions { Environment = QuietEnvironment, WorkingDirectory = Path.GetTempPath() }, cancellationToken);

    [GeneratedRegex(@"Logged in to (?<host>\S+) account (?<login>[A-Za-z0-9-]+)")]
    private static partial Regex LoggedIn();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex LoginPattern();
}
