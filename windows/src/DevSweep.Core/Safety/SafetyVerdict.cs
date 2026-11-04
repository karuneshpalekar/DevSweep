namespace DevSweep.Core.Safety;

/// <summary>Why the safety guard refused a path (or None when it allowed it).</summary>
public enum SafetyRefusal
{
    None,

    /// <summary>The path is not in the one unambiguous form the guard accepts.</summary>
    InvalidPath,

    /// <summary>Not inside the profile, a dev folder or the quarantine: shown as a command instead.</summary>
    OutsideAllowedRoots,

    /// <summary>The path is a protected folder.</summary>
    Protected,

    /// <summary>The path contains a protected folder (removing it would remove that too).</summary>
    ContainsProtected,

    /// <summary>A visible folder directly in the profile (such as <c>%USERPROFILE%\Projects</c>).</summary>
    TooShallow,

    /// <summary>The path goes through a junction or link.</summary>
    ThroughLink,

    /// <summary>The object opened is not at the path that was checked (a link or a swap in between).</summary>
    FinalPathMismatch,

    /// <summary>Nothing exists at the path.</summary>
    Missing,

    /// <summary>The item could not be opened for removal (in use without delete sharing, or access denied).</summary>
    CannotOpen,
}

/// <summary>The guard's answer for one path.</summary>
/// <param name="Path">The canonical, long-name path when it could be formed, otherwise the input.</param>
/// <param name="Refusal">Why it was refused, or None.</param>
/// <param name="Reason">A sentence for the user.</param>
public sealed record SafetyVerdict(string Path, SafetyRefusal Refusal, string Reason)
{
    public bool Allowed => Refusal == SafetyRefusal.None;

    public static SafetyVerdict Allow(string path) => new(path, SafetyRefusal.None, "Allowed.");
}
