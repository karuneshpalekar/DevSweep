using DevSweep.Core.Cleaning;

namespace DevSweep.Core.Health.Security;

public enum SecurityKind
{
    RecoveryCodes,
    PrivateKey,
    ServiceAccountKey,
    CloudAccessKeys,
    PasswordExport,
    EnvFile,

    /// <summary>A Remote Desktop file with a saved password.</summary>
    RdpPassword,

    /// <summary>An Azure <c>.publishsettings</c> file (deployment credentials).</summary>
    PublishSettings,

    /// <summary>A <c>.pfx</c>/<c>.p12</c> certificate with its private key, outside the certificate store.</summary>
    Certificate,

    /// <summary>A PuTTY private key (<c>.ppk</c>).</summary>
    PuttyKey,
}

public enum SecurityLevel
{
    Critical,
    Warning,
    Ok,
}

/// <summary>
/// A secret stored somewhere it shouldn't be. DevSweep records what kind of file it
/// is and where; it never keeps, logs or shows the secret itself.
/// </summary>
public sealed record SecurityFinding
{
    public string Id => "security:" + Path;

    public required SecurityKind Kind { get; init; }

    public required SecurityLevel Level { get; init; }

    public required string Title { get; init; }

    public required string Path { get; init; }

    /// <summary>Short status for the list ("Plain text", "Committed to git").</summary>
    public required string Status { get; init; }

    public required string Why { get; init; }

    public required string WhatToDo { get; init; }

    public IReadOnlyList<Check> Checks { get; init; } = [];

    /// <summary>PowerShell commands for fixes that are more than "move to the Recycle Bin".</summary>
    public IReadOnlyList<string>? Fix { get; init; }
}

/// <summary>What a security scan found, and what it could not look at.</summary>
public sealed record SecurityResult
{
    public IReadOnlyList<SecurityFinding> Findings { get; init; } = [];

    /// <summary>Folders looked in (Known Folders as redirected, and project roots).</summary>
    public IReadOnlyList<string> Looked { get; init; } = [];

    /// <summary>Cloud-only files (OneDrive and others) that were not opened, so not checked.</summary>
    public int CloudFilesSkipped { get; init; }

    /// <summary>Folders that could not be listed.</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

    /// <summary>git was not found or not trusted, so <c>.env</c> files' git status is unknown.</summary>
    public string? GitUnavailable { get; init; }

    public DateTimeOffset Date { get; init; }
}
