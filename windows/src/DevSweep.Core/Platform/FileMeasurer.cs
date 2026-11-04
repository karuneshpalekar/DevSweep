namespace DevSweep.Core.Platform;

/// <summary>
/// Measures one file found by a listing, the cheapest accurate way:
/// <list type="bullet">
/// <item>alternate streams wanted, or no file ID from the listing, or the file is
/// compressed, sparse or a reparse point: one metadata-only handle (<see cref="FileInspector"/>);</item>
/// <item>otherwise: <see cref="FileStat"/> by name (link count and fresh sizes, no handle),
/// with the 128-bit ID from the listing.</item>
/// </list>
/// The directory listing's own sizes are never used as the answer: NTFS updates
/// that copy lazily (and for hard links only under the name that was used), so
/// they are only compared, and differences counted as stale entries.
/// </summary>
internal sealed class FileMeasurer(bool includeStreams)
{
    private const Win32FileAttributes NeedsHandle =
        Win32FileAttributes.Compressed | Win32FileAttributes.SparseFile | Win32FileAttributes.ReparsePoint;

    private bool _statSupported = true;

    /// <summary>Files whose listing allocation differed from the fresh value.</summary>
    public long StaleEntries { get; private set; }

    public bool TryMeasure(string extendedPath, in DirectoryEntry entry, ulong? volumeSerial, out FileFacts facts, out int error)
    {
        if (!includeStreams && _statSupported && entry.FileId is { } id && volumeSerial is { } serial
            && (entry.Attributes & NeedsHandle) == 0)
        {
            if (FileStat.TryQuery(extendedPath, out StatFacts stat, out error))
            {
                if ((stat.Attributes & NeedsHandle) == 0)
                {
                    facts = new FileFacts(stat.Length, stat.AllocationSize, stat.LinkCount, new FileIdentity(serial, id));
                    NoteStale(in entry, stat.AllocationSize);
                    return true;
                }

                // Changed since it was listed (now compressed, sparse or a reparse point): measure it properly.
            }
            else if (Win32Errors.IsUnsupported(error))
            {
                _statSupported = false;
            }
            else
            {
                facts = default;
                return false;
            }
        }

        if (!FileInspector.TryInspect(extendedPath, in entry, includeStreams, out facts, out error))
        {
            return false;
        }

        if ((entry.Attributes & NeedsHandle) == 0)
        {
            NoteStale(in entry, facts.AllocationSize);
        }

        return true;
    }

    private void NoteStale(in DirectoryEntry entry, long freshAllocation)
    {
        if (entry.AllocationSize >= 0 && entry.AllocationSize != freshAllocation)
        {
            StaleEntries++;
        }
    }
}
