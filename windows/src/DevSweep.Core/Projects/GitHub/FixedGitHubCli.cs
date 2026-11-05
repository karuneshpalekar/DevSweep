namespace DevSweep.Core.Projects.GitHub;

/// <summary>A GitHub CLI with fixed accounts and tokens (tests, and the app's sample data).</summary>
public sealed class FixedGitHubCli(IReadOnlyList<GitHubAccount> accounts, IReadOnlyDictionary<string, string>? tokens = null, string? program = null) : IGitHubCli
{
    /// <summary>No gh at all.</summary>
    public static FixedGitHubCli None { get; } = new([]);

    public string? Program { get; } = program;

    /// <summary>Logins whose token was asked for, in order (to prove no other account was used).</summary>
    public List<string> TokensAskedFor { get; } = [];

    public Task<GitHubAccounts> AccountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(accounts.Count == 0
            ? new GitHubAccounts(GitHubStatus.NotSignedIn, [])
            : new GitHubAccounts(GitHubStatus.SignedIn, accounts));

    public Task<(GitHubToken? Token, string? Error)> TokenAsync(string login, CancellationToken cancellationToken = default)
    {
        TokensAskedFor.Add(login);
        return Task.FromResult(tokens is not null && tokens.TryGetValue(login, out string? token)
            ? ((GitHubToken?)new GitHubToken(login, token), (string?)null)
            : (null, $"No token for {login}."));
    }
}
