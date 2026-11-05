using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Health.Security;

/// <summary>
/// Looks in Downloads, Desktop and Documents (wherever they are redirected) and in project
/// folders for secrets left in plain files: recovery codes, private keys outside <c>.ssh</c>,
/// cloud service-account keys (also base64), AWS key CSVs, password-manager exports, <c>.env</c>
/// files (with their git status), and on Windows also <c>.rdp</c> files with saved passwords,
/// <c>.publishsettings</c>, <c>.pfx</c>/<c>.p12</c> outside the certificate store and PuTTY keys.
/// Files are recognised by name and their first few kilobytes; values are never kept, logged or
/// shown. Links are never followed and cloud-only files are never opened.
/// </summary>
public static partial class SecurityScanner
{
    public const int MaxRead = 64 * 1024;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "AppData", ".venv", "venv", "build", "dist", ".gradle", "bin", "obj", "target", "packages", "$RECYCLE.BIN",
    };

    private static readonly (string Key, string Name)[] Services =
    [
        ("github", "GitHub"), ("google", "Google"), ("twilio", "Twilio"), ("aws", "AWS"), ("amazon", "AWS"), ("microsoft", "Microsoft"),
        ("apple", "Apple"), ("gitlab", "GitLab"), ("slack", "Slack"), ("stripe", "Stripe"), ("firebase", "Firebase"), ("discord", "Discord"),
        ("binance", "Binance"), ("coinbase", "Coinbase"), ("azure", "Azure"),
    ];

    public static SecurityResult Scan(ISystemEnvironment environment, IReadOnlyList<string> projectRoots, IGitRunner? git, string? gitUnavailable = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(projectRoots);
        var state = new ScanState(environment.KnownFolder("Profile"), git);
        foreach (string name in (string[])["Downloads", "Desktop", "Documents"])
        {
            if (environment.KnownFolder(name) is { } folder && state.Looked.Add(folder))
            {
                Walk(state, folder, 2, onlyEnv: false);
            }
        }

        foreach (string root in projectRoots)
        {
            if (state.Looked.Add(root))
            {
                Walk(state, root, 4, onlyEnv: true);
            }
        }

        return new SecurityResult
        {
            Findings = state.Findings.Values.OrderBy(f => f.Level).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            Looked = state.Looked.ToList(),
            CloudFilesSkipped = state.CloudSkipped,
            Unreadable = state.Unreadable,
            GitUnavailable = git is null ? gitUnavailable ?? "git was not found" : null,
            Date = now ?? DateTimeOffset.UtcNow,
        };
    }

    private sealed class ScanState(string? profile, IGitRunner? git)
    {
        public string? Profile { get; } = profile;

        public IGitRunner? Git { get; } = git;

        public Dictionary<string, SecurityFinding> Findings { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Looked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Unreadable { get; } = [];

        public int CloudSkipped { get; set; }
    }

    private static void Walk(ScanState state, string folder, int depth, bool onlyEnv)
    {
        if (depth < 0)
        {
            return;
        }

        ProbeListing listing = FileProbe.List(folder);
        if (listing.Error is ProbeError.AccessDenied or ProbeError.Other)
        {
            state.Unreadable.Add(folder);
            return;
        }

        foreach (ProbeEntry entry in listing.Entries)
        {
            if (entry.IsDirectory)
            {
                bool hidden = entry.Name.StartsWith('.') && !entry.Name.Equals(".config", StringComparison.OrdinalIgnoreCase);
                if (entry.CanEnter && !hidden && !SkipDirs.Contains(entry.Name))
                {
                    Walk(state, entry.Path, depth - 1, onlyEnv);
                }

                continue;
            }

            if (onlyEnv && !IsEnvName(entry.Name))
            {
                continue;
            }

            if (entry.IsCloudPlaceholder)
            {
                state.CloudSkipped++;
                continue;
            }

            if (entry.IsReparsePoint)
            {
                continue;
            }

            SecurityFinding? finding = IsEnvName(entry.Name) ? EnvFinding(entry.Path, state.Git) : Classify(entry.Path, state.Profile);
            if (finding is not null)
            {
                state.Findings.TryAdd(finding.Path, finding);
            }
        }
    }

    private static bool Matches(string name, Regex pattern) => pattern.IsMatch(name);

    private static string? Service(string name)
        => Services.FirstOrDefault(s => name.Contains(s.Key, StringComparison.OrdinalIgnoreCase)).Name;

    private static string? Head(string path, int bytes = MaxRead)
        => HealthFiles.ReadHead(path, bytes) is { } data ? Encoding.UTF8.GetString(data) : null;

    /// <summary>What a file in a loose folder is, or null when it is nothing secret.</summary>
    public static SecurityFinding? Classify(string path, string? profile)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = System.IO.Path.GetFileName(path);
        string ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        string where = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        if (IsEnvName(name))
        {
            return null;
        }

        if (Matches(name, RecoveryName()) && ext is "txt" or "pdf" or "md" or "rtf" or "png" or "jpg" or "csv" or "")
        {
            string? svc = Service(name);
            return new SecurityFinding
            {
                Kind = SecurityKind.RecoveryCodes,
                Level = SecurityLevel.Critical,
                Title = $"{(svc is null ? string.Empty : svc + " ")}recovery codes",
                Path = path,
                Status = "Plain text",
                Why = $"Anyone who gets this file can sign in to your {svc ?? string.Empty} account without your password or phone.".Replace("  ", " ", StringComparison.Ordinal),
                WhatToDo = $"Save the codes in your password manager, then delete the file. {svc ?? "The service"} can also generate new codes, which makes these useless.",
                Checks = [Check.Passed("Recognised from the file name"), Check.Passed("The codes themselves were never read into DevSweep")],
            };
        }

        if (ext is "pfx" or "p12")
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.Certificate,
                Level = SecurityLevel.Warning,
                Title = "A certificate with its private key, outside the certificate store",
                Path = path,
                Status = "File",
                Why = $"A .{ext} file carries a private key. Lying in {where} it is easy to copy, sync or attach by accident, and it is only as safe as its password.",
                WhatToDo = "Import it into your certificate store (it keeps the key protected by Windows), then delete the file; or delete it if it is no longer used.",
                Checks = [Check.Passed("Recognised from the file type"), Check.Passed("The file was not opened")],
                Fix = [$"Import-PfxCertificate -FilePath {PowerShellText.Quote(path)} -CertStoreLocation Cert:\\CurrentUser\\My -Password (Read-Host -AsSecureString 'Certificate password')"],
            };
        }

        if (ext == "ppk")
        {
            string? text = Head(path, 4096);
            if (text?.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal) == true)
            {
                bool plain = text.Contains("Encryption: none", StringComparison.Ordinal);
                return KeyFinding(path, where, profile, SecurityKind.PuttyKey, plain ? "PuTTY private key without a passphrase" : "PuTTY private key (passphrase protected)", plain);
            }
        }

        if (ext is "pem" or "key" or "" || Matches(name, SshKeyName()))
        {
            string? text = Head(path, 4096);
            if (text is not null && text.Contains("-----BEGIN", StringComparison.Ordinal) && text.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            {
                bool encrypted = text.Contains("ENCRYPTED", StringComparison.Ordinal);
                return KeyFinding(path, where, profile, SecurityKind.PrivateKey, encrypted ? "A passphrase-protected private key outside .ssh" : "A private key outside .ssh", !encrypted);
            }
        }

        if (ext is "json" or "b64" or "txt" || Matches(name, CredentialName()))
        {
            if (HealthFiles.ReadHead(path, MaxRead) is { } data && IsServiceAccount(data))
            {
                return new SecurityFinding
                {
                    Kind = SecurityKind.ServiceAccountKey,
                    Level = SecurityLevel.Critical,
                    Title = $"{Service(name) ?? "Google Cloud"} service-account key",
                    Path = path,
                    Status = "Plain text",
                    Why = "It gives full access to whatever that service account can reach, with no password and no expiry.",
                    WhatToDo = "If you still need it, keep it outside Downloads (or in a secrets manager). If you don't, delete it and revoke the key in the cloud console.",
                    Checks = [Check.Passed("Recognised from the file's structure (a service-account key)"), Check.Passed("The key itself was never kept by DevSweep")],
                };
            }
        }

        if (ext == "csv" && Matches(name, AccessKeysName()) && Head(path, 400) is { } csv && csv.Contains("access key id", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.CloudAccessKeys,
                Level = SecurityLevel.Critical,
                Title = "AWS access keys",
                Path = path,
                Status = "Plain text",
                Why = "These keys can use your AWS account and run up costs if anyone gets them.",
                WhatToDo = "Store them with `aws configure` or a secrets manager, delete this file, and rotate the keys if the file was ever shared.",
                Checks = [Check.Passed("Recognised from the column headers")],
            };
        }

        if (ext is "csv" or "json" or "1pux" or "xml" && Matches(name, PasswordExportName()))
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.PasswordExport,
                Level = SecurityLevel.Critical,
                Title = "Password export",
                Path = path,
                Status = "Plain text",
                Why = "An export from a password manager or browser holds every saved password unencrypted.",
                WhatToDo = "Delete it once you've imported it where you needed it. If it was synced or shared, change the important passwords.",
                Checks = [Check.Passed("Recognised from the file name")],
            };
        }

        if (ext == "rdp" && Head(path) is { } rdp && RdpPassword().IsMatch(rdp))
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.RdpPassword,
                Level = SecurityLevel.Warning,
                Title = "Remote Desktop file with a saved password",
                Path = path,
                Status = "Saved password",
                Why = "The password is encrypted for your Windows account, but anyone signed in as you (or any program you run) can use the file to connect.",
                WhatToDo = "Remove the password from the file (save credentials in Windows Credential Manager instead), or delete the file if it is no longer used.",
                Checks = [Check.Passed("A saved-password line was found; its value was never kept")],
            };
        }

        if (ext == "publishsettings")
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.PublishSettings,
                Level = SecurityLevel.Critical,
                Title = "Azure publish profile",
                Path = path,
                Status = "Plain text",
                Why = "A .publishsettings file holds deployment credentials: anyone with it can deploy to (or read) that app or subscription.",
                WhatToDo = "Delete it and reset the publish profile in the Azure portal if it was ever shared.",
                Checks = [Check.Passed("Recognised from the file type"), Check.Passed("Its contents were never kept")],
            };
        }

        return null;
    }

    private static SecurityFinding KeyFinding(string path, string where, string? profile, SecurityKind kind, string title, bool critical)
    {
        string ssh = profile is null ? @"%USERPROFILE%\.ssh" : System.IO.Path.Join(profile, ".ssh");
        return new SecurityFinding
        {
            Kind = kind,
            Level = critical ? SecurityLevel.Critical : SecurityLevel.Warning,
            Title = title,
            Path = path,
            Status = critical ? "Exposed" : "Protected by a passphrase",
            Why = $"Private keys prove who you are to servers and services. One sitting in {where} is easy to copy, sync or attach by accident.",
            WhatToDo = $"Move it to {ssh} with permissions limited to you, or delete it if it's no longer used.",
            Checks = [Check.Passed("Recognised from the file's header"), Check.Passed("The key itself was never kept by DevSweep")],
            Fix =
            [
                $"New-Item -ItemType Directory -Force -Path {PowerShellText.Quote(ssh)} | Out-Null",
                $"Move-Item -LiteralPath {PowerShellText.Quote(path)} -Destination {PowerShellText.Quote(ssh)}",
                $"icacls {PowerShellText.Quote(System.IO.Path.Join(ssh, System.IO.Path.GetFileName(path)))} /inheritance:r /grant:r \"$($env:USERNAME):F\"",
            ],
        };
    }

    /// <summary>A Google-style service-account key: JSON with a private key and a client email or the service-account type (also base64-encoded).</summary>
    public static bool IsServiceAccount(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        static bool Check(string text) => text.Contains("\"private_key\"", StringComparison.Ordinal)
            && (text.Contains("service_account", StringComparison.Ordinal) || text.Contains("client_email", StringComparison.Ordinal));
        string text = Encoding.UTF8.GetString(data);
        if (Check(text))
        {
            return true;
        }

        string compact = new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length < 16 || compact.Length % 4 != 0)
        {
            return false;
        }

        byte[] decoded = new byte[compact.Length];
        return Convert.TryFromBase64String(compact, decoded, out int written) && Check(Encoding.UTF8.GetString(decoded, 0, written));
    }

    /// <summary><c>.env</c> and <c>.env.local</c>, not <c>.env.example</c> and other templates.</summary>
    public static bool IsEnvName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!name.Equals(".env", StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !((string[])["example", "sample", "template", "dist", "defaults"]).Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Counts secret-looking assignments (KEY=value where the key names a secret); values are never kept.</summary>
    public static int SecretCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int count = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.StartsWith('#') || eq <= 0)
            {
                continue;
            }

            string key = line[..eq].ToUpperInvariant();
            bool hasValue = line[(eq + 1)..].Trim().Trim('"', '\'').Length > 0;
            if (hasValue && ((string[])["SECRET", "TOKEN", "KEY", "PASSWORD", "PASS", "PRIVATE", "CREDENTIAL", "DSN"]).Any(k => key.Contains(k, StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>A <c>.env</c> file with secrets, and what git does with it (committed, ignored, or one <c>git add .</c> away).</summary>
    public static SecurityFinding? EnvFinding(string path, IGitRunner? git)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? text = Head(path);
        int count = text is null ? 0 : SecretCount(text);
        if (count == 0)
        {
            return null;
        }

        string dir = System.IO.Path.GetDirectoryName(path)!;
        string name = System.IO.Path.GetFileName(path);
        string project = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(dir));
        string keys = string.Create(CultureInfo.InvariantCulture, $"{count} secret{(count == 1 ? string.Empty : "s")}");
        Check valuesNeverKept = Check.Passed("Only key names were checked; values were never kept");
        if (git is null)
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.EnvFile, Level = SecurityLevel.Warning, Title = $"{name} with {keys} in {project}", Path = path, Status = "git status unknown",
                Why = "git could not be asked whether this file is committed or ignored.",
                WhatToDo = $"Make sure {name} is listed in .gitignore before you commit this project.",
                Checks = [Check.Warning("git was not available, so the file's git status is unknown"), valuesNeverKept],
            };
        }

        int? inside = git.ExitCode(dir, "rev-parse", "--is-inside-work-tree");
        if (inside is null)
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.EnvFile, Level = SecurityLevel.Warning, Title = $"{name} with {keys} in {project}", Path = path, Status = "git status unknown",
                Why = "git did not answer in time, so it is not known whether this file is committed or ignored.",
                WhatToDo = $"Make sure {name} is listed in .gitignore before you commit this project.",
                Checks = [Check.Warning("git did not answer"), valuesNeverKept],
            };
        }

        if (inside != 0)
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.EnvFile, Level = SecurityLevel.Warning, Title = $"{name} with {keys} in {project}", Path = path, Status = "Not in git",
                Why = "This folder isn't a git repository, so nothing protects the file if you later run git init and commit everything.",
                WhatToDo = $"When you put this project in git, add {name} to .gitignore first.",
                Checks = [valuesNeverKept],
            };
        }

        string cd = $"Set-Location -LiteralPath {PowerShellText.Quote(dir)}";
        if (git.ExitCode(dir, "ls-files", "--error-unmatch", "--", name) == 0)
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.EnvFile, Level = SecurityLevel.Critical, Title = $"{name} with {keys} is committed in {project}", Path = path, Status = "Committed to git",
                Why = "Everyone with access to this repository, now or later, can read these secrets, including in its history.",
                WhatToDo = "Stop tracking the file and ignore it, then rotate the secrets: removing it now doesn't remove it from past commits.",
                Checks = [Check.Warning("git tracks this file"), valuesNeverKept],
                Fix = [cd, $"git rm --cached -- {PowerShellText.Quote(name)}", $"Add-Content -LiteralPath .gitignore -Value {PowerShellText.Quote(name)}", $"git commit -m {PowerShellText.Quote("Stop tracking " + name)}"],
            };
        }

        if (git.ExitCode(dir, "check-ignore", "-q", "--", name) == 0)
        {
            return new SecurityFinding
            {
                Kind = SecurityKind.EnvFile, Level = SecurityLevel.Ok, Title = $"{name} with {keys} in {project}", Path = path, Status = "Kept out of git",
                Why = "This is the right way to keep local secrets: git ignores the file.",
                WhatToDo = "Nothing to do. Keep it out of shared folders and backups that sync publicly.",
                Checks = [Check.Passed("git ignores this file"), valuesNeverKept],
            };
        }

        return new SecurityFinding
        {
            Kind = SecurityKind.EnvFile, Level = SecurityLevel.Warning, Title = $"{name} with {keys} in {project} isn't ignored", Path = path, Status = "Not ignored",
            Why = "git doesn't track it yet, but it isn't in .gitignore either, so one `git add .` would commit your secrets.",
            WhatToDo = "Add it to .gitignore.",
            Checks = [Check.Warning("Not in .gitignore"), valuesNeverKept],
            Fix = [cd, $"Add-Content -LiteralPath .gitignore -Value {PowerShellText.Quote(name)}", $"git check-ignore -v -- {PowerShellText.Quote(name)}"],
        };
    }

    [GeneratedRegex(@"(recovery|backup)[-_ ]?codes?|2fa|two[-_ ]?factor", RegexOptions.IgnoreCase)]
    private static partial Regex RecoveryName();

    [GeneratedRegex(@"^id_(rsa|ed25519|ecdsa|dsa)$", RegexOptions.IgnoreCase)]
    private static partial Regex SshKeyName();

    [GeneratedRegex(@"credential|service[-_ ]?account|firebase|key", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialName();

    [GeneratedRegex(@"accesskeys?|credentials", RegexOptions.IgnoreCase)]
    private static partial Regex AccessKeysName();

    [GeneratedRegex(@"passwords?|bitwarden|1password|lastpass|keepass|logins", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordExportName();

    [GeneratedRegex(@"(?im)^\s*password 51:b:\s*\S+")]
    private static partial Regex RdpPassword();
}
