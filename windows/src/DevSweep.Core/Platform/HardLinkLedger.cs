namespace DevSweep.Core.Platform;

/// <summary>
/// Remembers files with more than one hard link so each is counted once per
/// walk, and works out which of them also live outside the walk.
/// </summary>
internal sealed class HardLinkLedger
{
    private readonly Dictionary<FileIdentity, Entry> _entries = [];

    /// <summary>Records a sighting. Returns true the first time this file is seen.</summary>
    public bool Register(in FileFacts facts)
    {
        if (facts.LinkCount <= 1)
        {
            return true;
        }

        if (_entries.TryGetValue(facts.Identity, out Entry entry))
        {
            _entries[facts.Identity] = entry with { NamesSeen = entry.NamesSeen + 1 };
            return false;
        }

        _entries[facts.Identity] = new Entry(1, facts.LinkCount, facts.AllocationSize);
        return true;
    }

    /// <summary>Files (and their bytes) with links the walk did not see.</summary>
    public (long Files, long Bytes) SharedOutside()
    {
        long files = 0;
        long bytes = 0;
        foreach (Entry entry in _entries.Values)
        {
            if (entry.NamesSeen < entry.LinkCount)
            {
                files++;
                bytes += entry.AllocationSize;
            }
        }

        return (files, bytes);
    }

    private readonly record struct Entry(uint NamesSeen, uint LinkCount, long AllocationSize);
}
