namespace DevSweep.Core.Projects.GitHub;

/// <summary>
/// One account's token from the GitHub CLI. Held only in memory for the calls that need
/// it, passed to child processes in their environment (never on a command line) and to
/// the API in a header. <see cref="ToString"/> never shows it, so it cannot end up in a log.
/// </summary>
public sealed class GitHubToken
{
    public GitHubToken(string login, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Login = login;
        Value = value.Trim();
    }

    public string Login { get; }

    /// <summary>The secret itself. Use only for an Authorization header or a child's environment.</summary>
    public string Value { get; }

    public override string ToString() => $"token for {Login} (hidden)";
}
