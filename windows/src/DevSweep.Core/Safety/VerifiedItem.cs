using DevSweep.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace DevSweep.Core.Safety;

/// <summary>
/// An item the guard checked, held open (DELETE access, as itself, never through
/// a link). Removing or moving it through <see cref="Handle"/> acts on exactly
/// the object that was checked, whatever happens to its path afterwards.
/// </summary>
public sealed class VerifiedItem : IDisposable
{
    internal VerifiedItem(SafeFileHandle handle, string path, Win32FileAttributes attributes, uint reparseTag)
    {
        Handle = handle;
        Path = path;
        Attributes = attributes;
        ReparseTag = reparseTag;
    }

    /// <summary>Canonical, long-name path that was checked.</summary>
    public string Path { get; }

    /// <summary>Attributes read from the handle.</summary>
    public Win32FileAttributes Attributes { get; }

    /// <summary>Reparse tag read from the handle (0 for none).</summary>
    public uint ReparseTag { get; }

    public bool IsDirectory => (Attributes & Win32FileAttributes.Directory) != 0;

    /// <summary>True when the item is itself a link (removing it removes the link, never its target).</summary>
    public bool IsLink => (Attributes & Win32FileAttributes.ReparsePoint) != 0
        && ReparseTags.Classify(ReparseTag) is not (ReparseKind.Wof or ReparseKind.Dedup or ReparseKind.Cloud);

    internal SafeFileHandle Handle { get; }

    /// <summary>
    /// Deletes the item for good through the checked handle: the object deleted is
    /// the one that was checked, even if its path was swapped since. A folder's
    /// contents go first (never following links); a link is removed as a link.
    /// </summary>
    public DeleteReport DeleteForGood() => TreeDeleter.DeleteItem(Handle, Path, IsDirectory && !IsLink);

    public void Dispose() => Handle.Dispose();
}
