namespace DevSweep.Core.Platform;

/// <summary>Where a file's data lives, as far as a cloud sync provider is concerned.</summary>
public enum CloudFileState
{
    /// <summary>An ordinary local file.</summary>
    NotCloud,

    /// <summary>Managed by a sync provider, with its data fully on this disk.</summary>
    LocallyAvailable,

    /// <summary>
    /// A placeholder: some or all data is only in the cloud. It takes (almost)
    /// no space here and must never be opened, because opening or reading it
    /// downloads it.
    /// </summary>
    Placeholder,
}

/// <summary>Classifies cloud files from directory-entry metadata alone, without opening them.</summary>
public static class CloudFiles
{
    private const Win32FileAttributes NotLocalBits =
        Win32FileAttributes.RecallOnDataAccess | Win32FileAttributes.RecallOnOpen | Win32FileAttributes.Offline;

    private const Win32FileAttributes PinBits = Win32FileAttributes.Pinned | Win32FileAttributes.Unpinned;

    public static CloudFileState Classify(Win32FileAttributes attributes, uint reparseTag = 0)
    {
        if ((attributes & NotLocalBits) != 0)
        {
            return CloudFileState.Placeholder;
        }

        bool cloudTagged = (attributes & Win32FileAttributes.ReparsePoint) != 0
            && ReparseTags.Classify(reparseTag) == ReparseKind.Cloud;
        return cloudTagged || (attributes & PinBits) != 0
            ? CloudFileState.LocallyAvailable
            : CloudFileState.NotCloud;
    }
}
