namespace DevSweep.Core.Platform;

/// <summary>What opening one directory gave.</summary>
/// <param name="Listing">The listing, or null.</param>
/// <param name="Error">The Win32 error when there is no listing and no <paramref name="Link"/>.</param>
/// <param name="Link">Set when the folder had become a link by the time it was opened: record it, never enter it.</param>
internal readonly record struct ListingOpen(IDirectoryListing? Listing, int Error, DirectoryEntry? Link);

/// <summary>
/// Opens directory listings for one walk: <see cref="IdExtdDirectoryListing"/>
/// while the file system supports it, then <see cref="FindFileEnumerator"/> for
/// the rest of the walk. A walk never crosses into another volume, so one
/// fallback decision per walk is enough.
/// </summary>
internal sealed class DirectoryLister(ListingMethod method)
{
    private bool _useIdExtd = method == ListingMethod.Auto;
    private long _idExtdDirectories;
    private long _findFileDirectories;

    /// <summary>Volume serial read from the first folder handle (matches <c>FILE_ID_INFO</c>), for file identities.</summary>
    public ulong? VolumeSerial { get; private set; }

    /// <summary>True when listings carry file IDs (so files need not be opened for one).</summary>
    public bool ListingsHaveIds => _useIdExtd;

    public ListingUsed Used => (_idExtdDirectories > 0, _findFileDirectories > 0) switch
    {
        (true, true) => ListingUsed.Both,
        (true, false) => ListingUsed.FileIdExtdDirectoryInfo,
        (false, true) => ListingUsed.FindFirstFile,
        _ => ListingUsed.None,
    };

    public ListingOpen Open(string extendedDirectory)
    {
        if (_useIdExtd)
        {
            ListingOpen? opened = TryIdExtd(extendedDirectory);
            if (opened is { } result)
            {
                return result;
            }

            _useIdExtd = false;
        }

        FindFileEnumerator? find = FindFileEnumerator.Open(extendedDirectory, out int error);
        if (find is not null)
        {
            _findFileDirectories++;
        }

        return new ListingOpen(find, error, null);
    }

    /// <summary>Null when the file system does not support the information class.</summary>
    private ListingOpen? TryIdExtd(string extendedDirectory)
    {
        var handle = NativeFile.Open(extendedDirectory, NativeFile.ListDirectory, NativeFile.NoFollow, out int error);
        if (handle is null)
        {
            return new ListingOpen(null, error, null);
        }

        // Check the object we hold, not the name we were given: if the folder was
        // replaced by a junction after its parent was listed, do not go in.
        if (NativeFile.TryGetAttributeTag(handle, out Win32FileAttributes attributes, out uint tag)
            && (attributes & Win32FileAttributes.ReparsePoint) != 0
            && ReparseTags.Classify(tag) != ReparseKind.Cloud)
        {
            handle.Dispose();
            var link = new DirectoryEntry(Path.GetFileName(LongPath.StripPrefix(extendedDirectory)), attributes, tag, 0);
            return new ListingOpen(null, 0, link);
        }

        switch (IdExtdDirectoryListing.TryCreate(handle, out IdExtdDirectoryListing? listing, out error))
        {
            case ListingStatus.Opened:
                _idExtdDirectories++;
                if (VolumeSerial is null && NativeFile.TryGetIdentity(handle, out FileIdentity identity, out _))
                {
                    VolumeSerial = identity.VolumeSerial;
                }

                return new ListingOpen(listing, 0, null);
            case ListingStatus.Unsupported:
                handle.Dispose();
                return null;
            default:
                handle.Dispose();
                return new ListingOpen(null, error, null);
        }
    }
}
