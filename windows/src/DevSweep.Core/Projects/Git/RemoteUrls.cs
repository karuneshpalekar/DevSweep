using System.Text.RegularExpressions;

namespace DevSweep.Core.Projects.Git;

/// <summary>A remote URL taken apart.</summary>
/// <param name="Host">The server, or null for a folder on this PC or a share.</param>
/// <param name="Path">The repository path on the server, or the folder.</param>
public sealed record RemoteUrl(string? Host, string Path)
{
    public bool IsLocalFolder => Host is null;
}

/// <summary>
/// Reads <c>owner/name</c> out of remote URLs and pasted text (the Mac <c>parseSlug</c>),
/// for https, <c>git@github.com:</c>, <c>ssh://</c> and SSH host aliases from
/// <c>~\.ssh\config</c> (<c>Host github-work</c> + <c>HostName github.com</c>, the usual
/// way to use two accounts over SSH). Credentials in a URL are dropped, never kept.
/// </summary>
public static partial class RemoteUrls
{
    public const string GitHubHost = "github.com";

    /// <summary>Takes a remote URL apart (scp-like <c>host:path</c>, <c>scheme://</c> URLs, or a local path).</summary>
    public static RemoteUrl Parse(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string s = url.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            string protocol = s[..scheme];
            string rest = s[(scheme + 3)..];
            if (protocol.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteUrl(null, rest);
            }

            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            string authority = slash < 0 ? rest : rest[..slash];
            string path = slash < 0 ? string.Empty : rest[(slash + 1)..];
            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                authority = authority[(at + 1)..];
            }

            int colon = authority.IndexOf(':', StringComparison.Ordinal);
            return new RemoteUrl((colon < 0 ? authority : authority[..colon]).ToLowerInvariant(), path);
        }

        // scp-like: [user@]host:path, where host has no slash and is not a drive letter.
        int c = s.IndexOf(':', StringComparison.Ordinal);
        if (c > 1 && s.AsSpan(0, c).IndexOfAny('/', '\\') < 0)
        {
            string host = s[..c];
            int at = host.LastIndexOf('@');
            return new RemoteUrl((at >= 0 ? host[(at + 1)..] : host).ToLowerInvariant(), s[(c + 1)..]);
        }

        return new RemoteUrl(null, s);
    }

    /// <summary><c>owner/name</c> when <paramref name="url"/> is a GitHub remote, else null.</summary>
    public static string? GitHubSlug(string url, IReadOnlySet<string>? sshAliases = null)
    {
        RemoteUrl remote = Parse(url);
        if (remote.Host is not { } host)
        {
            return null;
        }

        bool github = host is GitHubHost or "www.github.com" or "ssh.github.com" || (sshAliases?.Contains(host) ?? false);
        return github ? SlugFromPath(remote.Path) : null;
    }

    /// <summary>
    /// <c>owner/name</c> from pasted text: <c>owner/name</c>, <c>github.com/owner/name</c>, an
    /// https or SSH URL (with or without <c>.git</c>, credentials stripped), or null.
    /// </summary>
    public static string? ParseSlug(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string s = input.Trim();
        if (s.Contains("://", StringComparison.Ordinal) || s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubSlug(s);
        }

        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["github.com/".Length..];
        }

        return SlugFromPath(s);
    }

    /// <summary>Hosts in an OpenSSH config whose <c>HostName</c> is github.com.</summary>
    public static IReadOnlySet<string> SshAliases(string? sshConfigText)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> current = [];
        foreach (string raw in (sshConfigText ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split((char[])[' ', '\t', '='], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string value = parts[1].Trim().Trim('"');
            if (parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                current = value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(h => !h.Contains('*', StringComparison.Ordinal)).ToList();
            }
            else if (parts[0].Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
            }
            else if (parts[0].Equals("HostName", StringComparison.OrdinalIgnoreCase) && value.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase))
            {
                aliases.UnionWith(current);
            }
        }

        return aliases;
    }

    /// <summary>The SSH aliases of the signed-in user (<c>%USERPROFILE%\.ssh\config</c>); empty when unreadable.</summary>
    public static IReadOnlySet<string> SshAliasesFromProfile(string? profile)
    {
        if (profile is null)
        {
            return new HashSet<string>();
        }

        try
        {
            string file = System.IO.Path.Join(profile, ".ssh", "config");
            return File.Exists(file) ? SshAliases(File.ReadAllText(file)) : new HashSet<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }

    private static string? SlugFromPath(string path)
    {
        string s = path.Trim().Trim('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        string[] parts = s.Split('/');
        return parts.Length == 2 && parts.All(p => SlugPart().IsMatch(p)) ? $"{parts[0]}/{parts[1]}" : null;
    }

    [GeneratedRegex(@"^[\w.-]+$")]
    private static partial Regex SlugPart();
}
