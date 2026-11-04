using DevSweep.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace DevSweep.Core.Safety;

/// <summary>
/// The last line of defence before anything is removed or moved, whatever a
/// rule says (PLAN 4.4). In order:
/// <list type="number">
/// <item>the path must be in the one canonical form (<see cref="SafePath"/>); <c>..</c> and friends are refused;</item>
/// <item>8.3 short names are expanded against the disk (<c>GetLongPathNameW</c>);</item>
/// <item>the policy: only inside the profile, a dev folder or the quarantine, never a protected folder or one containing it;</item>
/// <item>no junction or link on the way (a volume mounted in a folder is allowed, never crossed);</item>
/// <item>the item is opened as itself and <c>GetFinalPathNameByHandleW</c> must give back the
/// same path, so a link or a swap between the check and the open is caught; removal then goes
/// through that handle (<see cref="TryOpen"/>).</item>
/// </list>
/// </summary>
public sealed class SafetyGuard(SafetyContext context)
{
    public SafetyContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>The guard for the signed-in user.</summary>
    public static SafetyGuard ForCurrentUser(IEnumerable<string>? devRoots = null, IEnumerable<string>? quarantineRoots = null)
        => new(SafetyContextFactory.ForCurrentUser(devRoots, quarantineRoots));

    /// <summary>Checks <paramref name="path"/> without keeping it open.</summary>
    public SafetyVerdict Check(string path)
    {
        SafetyVerdict verdict = TryOpen(path, out VerifiedItem? item);
        item?.Dispose();
        return verdict;
    }

    /// <summary>
    /// Checks <paramref name="path"/> and, when allowed, returns it open for
    /// removal. The caller disposes <paramref name="item"/>.
    /// </summary>
    public SafetyVerdict TryOpen(string path, out VerifiedItem? item)
    {
        item = null;
        SafetyVerdict lexical = CheckLexically(path);
        if (!lexical.Allowed)
        {
            return lexical;
        }

        string canonical = lexical.Path;
        if (ReparseGuard.FindReparseAncestor(canonical) is { } link)
        {
            return new SafetyVerdict(canonical, SafetyRefusal.ThroughLink,
                $"{canonical} is reached through a junction or link ({link}); DevSweep never removes anything through one.");
        }

        string extended = LongPath.ToExtended(canonical);
        SafeFileHandle? handle = NativeFile.Open(extended, NativeFile.DeleteAccess, NativeFile.NoFollow, out int error);
        if (handle is null)
        {
            return Win32Errors.IsNotFound(error)
                ? new SafetyVerdict(canonical, SafetyRefusal.Missing, $"Nothing exists at {canonical}.")
                : new SafetyVerdict(canonical, SafetyRefusal.CannotOpen, OpenFailure(canonical, error));
        }

        if (!FinalPath.Matches(handle, canonical, out string? actual))
        {
            handle.Dispose();
            return new SafetyVerdict(canonical, SafetyRefusal.FinalPathMismatch,
                $"{canonical} resolves to {actual ?? "an unknown place"} when opened, so something on the way is a link or was swapped; nothing was changed.");
        }

        if (!NativeFile.TryGetAttributeTag(handle, out Win32FileAttributes attributes, out uint tag))
        {
            // FAT and some network file systems: the directory entry says the same (they have no reparse points).
            FindFileEnumerator.TryReadEntry(extended, out DirectoryEntry entry, out _);
            attributes = entry.Attributes;
            tag = entry.ReparseTag;
        }

        item = new VerifiedItem(handle, canonical, attributes, tag);
        return lexical;
    }

    /// <summary>Steps 1-3 only (no disk access beyond short-name expansion).</summary>
    public SafetyVerdict CheckLexically(string path)
    {
        if (!SafePath.TryCanonicalize(path, out string? canonical, out string rejection))
        {
            return new SafetyVerdict(path ?? string.Empty, SafetyRefusal.InvalidPath, rejection);
        }

        string resolved = SafetyContextFactory.Canonicalise(canonical);
        return SafetyPolicy.Evaluate(Context, resolved);
    }

    private static string OpenFailure(string path, int error) => error switch
    {
        Win32Errors.SharingViolation => $"{path} is in use by another program that does not allow it to be removed.",
        Win32Errors.AccessDenied => $"Access to {path} is denied; DevSweep never takes ownership or changes permissions.",
        _ => $"{path} could not be opened (Win32 error {error}).",
    };
}
