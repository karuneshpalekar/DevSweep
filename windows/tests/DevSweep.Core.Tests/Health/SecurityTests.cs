using System.Text;
using System.Text.Json;
using DevSweep.Core.Health.Security;
using Xunit;

namespace DevSweep.Core.Tests.Health;

/// <summary>Synthetic fake secrets only. Every value below is made up; none is ever allowed into a finding.</summary>
public sealed class SecurityTests
{
    private const string FakeAwsKey = "AKIAFAKEFAKEFAKE1234";
    private const string FakeSecret = "fake-secret-value-do-not-use";
    private const string FakeServiceAccount = """{"type":"service_account","project_id":"fake","private_key_id":"abc","private_key":"-----BEGIN PRIVATE KEY-----\nFAKEKEYDATA\n-----END PRIVATE KEY-----\n","client_email":"fake@fake.iam.gserviceaccount.com"}""";

    [Fact]
    public void Each_kind_of_secret_is_recognised_in_the_loose_folders()
    {
        using var machine = new HealthMachine();
        string downloads = machine.Environment.KnownFolder("Downloads")!;
        string desktop = machine.Environment.KnownFolder("Desktop")!;
        string documents = machine.Environment.KnownFolder("Documents")!;
        machine.File(Path.Join(downloads, "github-recovery-codes.txt"), "abcde-12345\nfghij-67890\n");
        machine.File(Path.Join(downloads, "id_ed25519"), "-----BEGIN OPENSSH PRIVATE KEY-----\nFAKEFAKEFAKE\n-----END OPENSSH PRIVATE KEY-----\n");
        machine.File(Path.Join(downloads, "old.key"), "-----BEGIN ENCRYPTED PRIVATE KEY-----\nFAKE\n-----END ENCRYPTED PRIVATE KEY-----\n");
        machine.File(Path.Join(downloads, "my-project-1234.json"), FakeServiceAccount);
        machine.File(Path.Join(downloads, "sa.b64"), Convert.ToBase64String(Encoding.UTF8.GetBytes(FakeServiceAccount)));
        machine.File(Path.Join(downloads, "rootkey_accessKeys.csv"), $"Access key ID,Secret access key\n{FakeAwsKey},{FakeSecret}\n");
        machine.File(Path.Join(desktop, "bitwarden_export_2026.json"), "{\"items\":[]}");
        machine.File(Path.Join(desktop, "Chrome Passwords.csv"), $"name,url,username,password\nx,https://x,me,{FakeSecret}\n");
        machine.File(Path.Join(desktop, "server.rdp"), $"full address:s:10.0.0.5\r\nusername:s:admin\r\npassword 51:b:01000000D08C9DDF0115D1118C7A00C04FC297EB{FakeSecret}\r\n");
        machine.File(Path.Join(desktop, "plain.rdp"), "full address:s:10.0.0.6\r\nusername:s:admin\r\n");
        machine.File(Path.Join(documents, "Azure", "webapp.PublishSettings"), $"<publishData><publishProfile userPWD=\"{FakeSecret}\"/></publishData>");
        machine.File(Path.Join(documents, "signing.pfx"), "binary");
        machine.File(Path.Join(documents, "server.ppk"), $"PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\nPrivate-Lines: 1\n{FakeSecret}\n");
        machine.File(Path.Join(documents, "laptop.ppk"), "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\n");
        machine.File(Path.Join(documents, "notes.txt"), "nothing to see");
        machine.File(Path.Join(documents, "deep", "a", "b", "c", "id_rsa"), "-----BEGIN RSA PRIVATE KEY-----\nFAKE\n-----END RSA PRIVATE KEY-----\n");
        machine.File(Path.Join(documents, ".ssh", "id_rsa"), "-----BEGIN RSA PRIVATE KEY-----\nFAKE\n-----END RSA PRIVATE KEY-----\n");

        SecurityResult result = SecurityScanner.Scan(machine.Environment, [], git: null);

        Dictionary<string, SecurityFinding> byName = result.Findings.ToDictionary(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal((SecurityKind.RecoveryCodes, SecurityLevel.Critical, "GitHub recovery codes"), K(byName["github-recovery-codes.txt"]));
        Assert.Equal((SecurityKind.PrivateKey, SecurityLevel.Critical), (byName["id_ed25519"].Kind, byName["id_ed25519"].Level));
        Assert.Equal((SecurityKind.PrivateKey, SecurityLevel.Warning), (byName["old.key"].Kind, byName["old.key"].Level));
        Assert.Equal(SecurityKind.ServiceAccountKey, byName["my-project-1234.json"].Kind);
        Assert.Equal(SecurityKind.ServiceAccountKey, byName["sa.b64"].Kind);
        Assert.Equal(SecurityKind.CloudAccessKeys, byName["rootkey_accessKeys.csv"].Kind);
        Assert.Equal(SecurityKind.PasswordExport, byName["bitwarden_export_2026.json"].Kind);
        Assert.Equal(SecurityKind.PasswordExport, byName["Chrome Passwords.csv"].Kind);
        Assert.Equal((SecurityKind.RdpPassword, SecurityLevel.Warning), (byName["server.rdp"].Kind, byName["server.rdp"].Level));
        Assert.Equal(SecurityKind.PublishSettings, byName["webapp.PublishSettings"].Kind);
        Assert.Equal(SecurityKind.Certificate, byName["signing.pfx"].Kind);
        Assert.Equal((SecurityKind.PuttyKey, SecurityLevel.Critical), (byName["server.ppk"].Kind, byName["server.ppk"].Level));
        Assert.Equal((SecurityKind.PuttyKey, SecurityLevel.Warning), (byName["laptop.ppk"].Kind, byName["laptop.ppk"].Level));
        Assert.False(byName.ContainsKey("plain.rdp"));
        Assert.False(byName.ContainsKey("notes.txt"));
        Assert.False(byName.ContainsKey("id_rsa")); // depth 2 limit, and .ssh is never looked in
        Assert.Equal(13, result.Findings.Count);
        Assert.Equal(SecurityLevel.Critical, result.Findings[0].Level);

        string everything = JsonSerializer.Serialize(result.Findings.Select(f => new[] { f.Title, f.Path, f.Status, f.Why, f.WhatToDo, string.Join(" ", f.Fix ?? []), string.Join(" ", f.Checks.Select(c => c.Text)) }));
        Assert.DoesNotContain(FakeSecret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeAwsKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKEKEYDATA", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("abcde-12345", everything, StringComparison.Ordinal);
    }

    private static (SecurityKind, SecurityLevel, string) K(SecurityFinding f) => (f.Kind, f.Level, f.Title);

    [Fact]
    public void Env_files_get_their_git_status_and_templates_are_skipped()
    {
        using var machine = new HealthMachine();
        string root = Path.Join(machine.Profile, "source");
        string committed = Path.Join(root, "committed");
        string ignored = Path.Join(root, "ignored");
        string open = Path.Join(root, "open");
        string plain = Path.Join(root, "plain");
        foreach (string p in (string[])[committed, ignored, open, plain])
        {
            machine.File(Path.Join(p, ".env"), $"# comment\nAPI_KEY={FakeSecret}\nDB_PASSWORD=\"{FakeSecret}\"\nPORT=3000\nEMPTY_TOKEN=\n");
        }

        machine.File(Path.Join(open, ".env.example"), $"API_KEY={FakeSecret}\n");
        machine.File(Path.Join(open, ".env.local"), "NOTHING=1\n");
        var git = new FixedGitRunner();
        foreach (string p in (string[])[committed, ignored, open])
        {
            git.Answers[p + "|rev-parse --is-inside-work-tree"] = 0;
            git.Answers[p + "|ls-files --error-unmatch -- .env"] = p == committed ? 0 : 1;
            git.Answers[p + "|check-ignore -q -- .env"] = p == ignored ? 0 : 1;
        }

        git.Answers[plain + "|rev-parse --is-inside-work-tree"] = 128;

        SecurityResult result = SecurityScanner.Scan(machine.Environment, [root], git);

        SecurityFinding Of(string folder) => Assert.Single(result.Findings, f => f.Path == Path.Join(folder, ".env"));
        Assert.Equal((SecurityLevel.Critical, "Committed to git"), (Of(committed).Level, Of(committed).Status));
        Assert.Equal("git rm --cached -- '.env'", Of(committed).Fix![1]);
        Assert.Equal((SecurityLevel.Ok, "Kept out of git"), (Of(ignored).Level, Of(ignored).Status));
        Assert.Equal((SecurityLevel.Warning, "Not ignored"), (Of(open).Level, Of(open).Status));
        Assert.Equal((SecurityLevel.Warning, "Not in git"), (Of(plain).Level, Of(plain).Status));
        Assert.Contains("2 secrets", Of(open).Title, StringComparison.Ordinal);
        Assert.Equal(4, result.Findings.Count);
        Assert.DoesNotContain(FakeSecret, JsonSerializer.Serialize(result.Findings.Select(f => f.Title + f.Why + f.WhatToDo + string.Join(" ", f.Fix ?? []))), StringComparison.Ordinal);

        SecurityResult noGit = SecurityScanner.Scan(machine.Environment, [root], git: null, gitUnavailable: "git.exe was not found");
        Assert.All(noGit.Findings, f => Assert.Equal("git status unknown", f.Status));
        Assert.Equal("git.exe was not found", noGit.GitUnavailable);
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.production", true)]
    [InlineData(".env.example", false)]
    [InlineData(".env.sample", false)]
    [InlineData("env", false)]
    [InlineData(".environment", false)]
    public void Env_names(string name, bool expected) => Assert.Equal(expected, SecurityScanner.IsEnvName(name));

    [Fact]
    public void Secret_counting_reads_key_names_only()
        => Assert.Equal(3, SecurityScanner.SecretCount("A_TOKEN=x\n#SECRET=y\nPASSWORD = 'z'\nNAME=v\nDSN=postgres://\nKEY=\n"));
}
