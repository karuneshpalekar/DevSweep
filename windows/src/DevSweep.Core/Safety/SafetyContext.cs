namespace DevSweep.Core.Safety;

/// <summary>A folder DevSweep never removes, nor anything that contains it.</summary>
/// <param name="Path">Canonical, long-name path.</param>
/// <param name="Why">What it is, for the refusal message ("your Documents folder").</param>
public sealed record ProtectedPath(string Path, string Why);

/// <summary>
/// Where DevSweep may act and what it must never remove (PLAN 4.4). All paths
/// are canonical (<see cref="SafePath"/>) with short names already resolved.
/// Built for the current user by <see cref="SafetyContextFactory"/>; tests build their own.
/// </summary>
public sealed record SafetyContext
{
    /// <summary>The user profile (<c>%USERPROFILE%</c>).</summary>
    public required string ProfileRoot { get; init; }

    /// <summary>Folders the user chose as their own development folders (items inside may be removed).</summary>
    public IReadOnlyList<string> DevRoots { get; init; } = [];

    /// <summary>DevSweep's quarantine folders (items inside may be restored or removed for good).</summary>
    public IReadOnlyList<string> QuarantineRoots { get; init; } = [];

    /// <summary>Protected folders: never removed, nor any folder containing one.</summary>
    public IReadOnlyList<ProtectedPath> Protected { get; init; } = [];

    /// <summary>
    /// A context from raw paths: each is canonicalised (unusable ones dropped),
    /// and the profile, dev roots and quarantine roots are protected themselves.
    /// </summary>
    public static SafetyContext Create(
        string profileRoot,
        IEnumerable<ProtectedPath> protectedPaths,
        IEnumerable<string>? devRoots = null,
        IEnumerable<string>? quarantineRoots = null,
        Func<string, string>? resolveLongNames = null)
    {
        ArgumentNullException.ThrowIfNull(protectedPaths);
        Func<string, string> resolve = resolveLongNames ?? (p => p);
        string profile = Canonical(profileRoot, resolve)
            ?? throw new ArgumentException($"The profile path '{profileRoot}' is not a usable full path.", nameof(profileRoot));
        List<string> dev = [.. (devRoots ?? []).Select(p => Canonical(p, resolve)).OfType<string>()];
        List<string> quarantine = [.. (quarantineRoots ?? []).Select(p => Canonical(p, resolve)).OfType<string>()];

        var all = new List<ProtectedPath> { new(profile, "your user profile folder") };
        foreach (ProtectedPath item in protectedPaths)
        {
            if (Canonical(item.Path, resolve) is { } path)
            {
                all.Add(item with { Path = path });
            }
        }

        all.AddRange(dev.Select(d => new ProtectedPath(d, "one of your development folders")));
        all.AddRange(quarantine.Select(q => new ProtectedPath(q, "DevSweep's quarantine folder")));
        return new SafetyContext
        {
            ProfileRoot = profile,
            DevRoots = dev,
            QuarantineRoots = quarantine,
            Protected = [.. all.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static string? Canonical(string path, Func<string, string> resolve)
        => SafePath.TryCanonicalize(path, out string? canonical, out _) ? resolve(canonical) : null;
}
