using DevSweep.Core.Projects;

namespace DevSweep.App.ViewModels.Settings;

/// <summary>Checks a GitHub account's commit identity before it is saved (Settings, GitHub accounts; Projects, Accounts).</summary>
public static class IdentityValidation
{
    /// <summary>Null when the identity can be saved: both blank (clears it) or a name and a plausible email. Else what to fix, in words.</summary>
    public static string? Check(string name, string email)
    {
        string n = (name ?? string.Empty).Trim();
        string e = (email ?? string.Empty).Trim();
        if (n.Length == 0 && e.Length == 0)
        {
            return null;
        }

        if (n.Length == 0)
        {
            return "Add the name to show on commits.";
        }

        if (e.Length == 0)
        {
            return "Add the email to show on commits.";
        }

        return IsEmail(e) ? null : "That doesn't look like an email address (name@example.com).";
    }

    public static string? Check(GitIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Check(identity.Name, identity.Email);
    }

    /// <summary>One at sign with something before it, and a dotted domain after it; no spaces, no angle brackets.</summary>
    public static bool IsEmail(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or ',' or ';'))
        {
            return false;
        }

        int at = text.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != text.LastIndexOf('@'))
        {
            return false;
        }

        string domain = text[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal) && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..", StringComparison.Ordinal);
    }
}
