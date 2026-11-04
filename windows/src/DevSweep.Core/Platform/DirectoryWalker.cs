using System.ComponentModel;
using System.Diagnostics;

namespace DevSweep.Core.Platform;

/// <summary>
/// Measures a file or folder the way the disk sees it (PLAN 4.2):
/// metadata-only enumeration under <c>\\?\</c> paths, on-disk allocation,
/// hard links counted once, reparse points recorded and never followed,
/// cloud placeholders counted as 0 and never opened, and a time limit that
/// returns partial counts instead of nothing.
/// </summary>
public static class DirectoryWalker
{
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="Win32Exception">The path itself cannot be read.</exception>
    public static WalkResult Measure(string path, WalkOptions? options = null, CancellationToken cancellationToken = default)
    {
        string display = LongPath.Normalize(path);
        string extended = LongPath.ToExtended(display);
        var walk = new Walk(options ?? WalkOptions.Default, cancellationToken);

        if (LongPath.IsRoot(display))
        {
            walk.WalkTree(extended);
        }
        else if (FindFileEnumerator.TryReadEntry(extended, out DirectoryEntry entry, out int error))
        {
            walk.VisitRoot(extended, in entry);
        }
        else if (Win32Errors.IsNotFound(error))
        {
            throw new FileNotFoundException($"Nothing exists at {display}", display);
        }
        else
        {
            throw new Win32Exception(error, $"Cannot read {display}");
        }

        return walk.ToResult(display);
    }

    /// <summary>The state of one walk. Single-threaded; one instance per call.</summary>
    private sealed class Walk(WalkOptions options, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly HardLinkLedger _ledger = new();
        private readonly List<ReparsePointRecord> _reparsePoints = [];
        private readonly List<WalkProblem> _problems = [];
        private bool _stopped;
        private long _bytes;
        private long _logicalBytes;
        private long _files;
        private long _directories;
        private long _duplicates;
        private long _placeholders;
        private long _placeholderLogicalBytes;
        private long _accessDenied;
        private long _unreadable;
        private long _reparsePointCount;

        public void VisitRoot(string extendedPath, in DirectoryEntry entry)
        {
            if (ShouldStop())
            {
                return;
            }

            if (!entry.IsDirectory)
            {
                VisitFile(extendedPath, in entry);
            }
            else if (entry.IsReparsePoint)
            {
                RecordReparsePoint(extendedPath, in entry);
            }
            else
            {
                WalkTree(extendedPath);
            }
        }

        public void WalkTree(string extendedRoot)
        {
            var pending = new Stack<string>();
            pending.Push(extendedRoot);
            while (pending.Count > 0 && !ShouldStop())
            {
                string directory = pending.Pop();
                _directories++;
                using FindFileEnumerator? listing = FindFileEnumerator.Open(directory, out int error);
                if (listing is null)
                {
                    RecordProblem(directory, error);
                    continue;
                }

                while (listing.MoveNext(out DirectoryEntry entry))
                {
                    if (ShouldStop())
                    {
                        return;
                    }

                    string child = LongPath.Join(directory, entry.Name);
                    if (!entry.IsDirectory)
                    {
                        VisitFile(child, in entry);
                    }
                    else if (entry.IsReparsePoint)
                    {
                        // Junctions, symlinks, mount points, cloud and projected folders: never entered.
                        RecordReparsePoint(child, in entry);
                    }
                    else
                    {
                        pending.Push(child);
                    }
                }

                if (listing.Error != 0)
                {
                    RecordProblem(directory, listing.Error);
                }
            }
        }

        public WalkResult ToResult(string displayPath)
        {
            (long sharedFiles, long sharedBytes) = _ledger.SharedOutside();
            return new WalkResult
            {
                Path = displayPath,
                Bytes = _bytes,
                LogicalBytes = _logicalBytes,
                Files = _files,
                Directories = _directories,
                Complete = !_stopped,
                HardLinkDuplicates = _duplicates,
                SharedLinkBytes = sharedBytes,
                SharedLinkFiles = sharedFiles,
                CloudPlaceholderCount = _placeholders,
                CloudPlaceholderLogicalBytes = _placeholderLogicalBytes,
                AccessDenied = _accessDenied,
                Unreadable = _unreadable,
                ReparsePointCount = _reparsePointCount,
                ReparsePoints = _reparsePoints,
                Problems = _problems,
                Elapsed = _clock.Elapsed,
            };
        }

        private void VisitFile(string extendedPath, in DirectoryEntry entry)
        {
            if (entry.CloudState == CloudFileState.Placeholder)
            {
                // Never opened: opening a recall-on-open placeholder downloads it.
                _placeholders++;
                _placeholderLogicalBytes += entry.Length;
                return;
            }

            if (entry.IsLink)
            {
                RecordReparsePoint(extendedPath, in entry);
                return;
            }

            if (!FileInspector.TryInspect(extendedPath, in entry, out FileFacts facts, out int error))
            {
                RecordProblem(extendedPath, error);
                return;
            }

            if (_ledger.Register(in facts))
            {
                _files++;
                _bytes += facts.AllocationSize;
                _logicalBytes += facts.Length;
            }
            else
            {
                _duplicates++;
            }
        }

        private void RecordReparsePoint(string extendedPath, in DirectoryEntry entry)
        {
            _reparsePointCount++;
            if (_reparsePoints.Count < options.MaxRecordedReparsePoints)
            {
                _reparsePoints.Add(new ReparsePointRecord(
                    LongPath.StripPrefix(extendedPath), entry.ReparseTag, entry.ReparseKind, entry.IsDirectory));
            }
        }

        private void RecordProblem(string extendedPath, int error)
        {
            if (Win32Errors.IsNotFound(error))
            {
                return; // Removed while we walked: nothing to count.
            }

            if (error == Win32Errors.AccessDenied)
            {
                _accessDenied++;
            }
            else
            {
                _unreadable++;
            }

            if (_problems.Count < options.MaxRecordedProblems)
            {
                _problems.Add(new WalkProblem(LongPath.StripPrefix(extendedPath), error));
            }
        }

        private bool ShouldStop()
        {
            if (!_stopped && (cancellationToken.IsCancellationRequested
                || (options.TimeLimit is { } limit && _clock.Elapsed >= limit)))
            {
                _stopped = true;
            }

            return _stopped;
        }
    }
}
