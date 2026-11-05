using DevSweep.Core.Health.Security;

namespace DevSweep.Core.Projects.Actions;

/// <summary>A file that looks like a secret and would be published.</summary>
/// <param name="Path">Full path.</param>
/// <param name="What">What it looks like ("Private key without a passphrase", ".env with 3 secrets").</param>
public sealed record SecretFile(string Path, string What);

/// <summary>Checks files before they are published. The values are never kept or shown.</summary>
public interface ISecretCheck
{
    IReadOnlyList<SecretFile> Check(IReadOnlyList<string> files, string? profile);
}

/// <summary>
/// Health's security scanner (M3b), applied to exactly the files a publish would upload:
/// private keys, recovery codes, service-account and cloud keys, password exports,
/// certificates, PuTTY keys, <c>.rdp</c>/<c>.publishsettings</c> files, and <c>.env</c>
/// files with secret-looking values. Files are recognised by name and their first few
/// kilobytes; links and cloud-only files are never opened.
/// </summary>
public sealed class HealthSecretCheck : ISecretCheck
{
    public static HealthSecretCheck Instance { get; } = new();

    public IReadOnlyList<SecretFile> Check(IReadOnlyList<string> files, string? profile)
    {
        ArgumentNullException.ThrowIfNull(files);
        var found = new List<SecretFile>();
        foreach (string file in files)
        {
            SecurityFinding? finding = SecurityScanner.IsEnvName(Path.GetFileName(file))
                ? SecurityScanner.EnvFinding(file, git: null)
                : SecurityScanner.Classify(file, profile);
            if (finding is not null && finding.Level != SecurityLevel.Ok)
            {
                found.Add(new SecretFile(file, finding.Title));
            }
        }

        return found;
    }
}
