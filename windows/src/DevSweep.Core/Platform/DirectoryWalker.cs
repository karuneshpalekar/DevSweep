using System.ComponentModel;
using System.Diagnostics;

namespace DevSweep.Core.Platform;

/// <summary>
/// Measures a file or folder the way the disk sees it (PLAN 4.2):
/// metadata-only enumeration from each folder's handle under <c>\\?\</c> paths,
/// on-disk allocation including alternate streams, hard links counted once,
/// reparse points recorded and never followed (cloud-provider folders excepted:
/// they are the user's real folders and are entered reading attributes only),
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
        private readonly DirectoryLister _lister = new(options.Listing);
        private readonly FileMeasurer _measurer = new(options.AlternateStreams);
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
        private long _cloudDirectories;
        private long _streams;
        private long _streamBytes;
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
            else
            {
                VisitDirectory(extendedPath, in entry, null);
            }
        }

        public void WalkTree(string extendedRoot)
        {
            var pending = new Stack<string>();
            pending.Push(extendedRoot);
            while (pending.Count > 0 && !ShouldStop())
            {
                string directory = pending.Pop();
                ListingOpen opened = _lister.Open(directory);
                if (opened.Link is { } link)
                {
                    RecordReparsePoint(directory, in link);
                    continue;
                }

                using IDirectoryListing? listing = opened.Listing;
                if (listing is null)
                {
                    RecordProblem(directory, opened.Error);
                    continue;
                }

                _directories++;
                while (listing.MoveNext(out DirectoryEntry entry))
                {
                    if (ShouldStop())
                    {
                        return;
                    }

                    string child = LongPath.Join(directory, entry.Name);
                    if (entry.IsDirectory)
                    {
                        VisitDirectory(child, in entry, pending);
                    }
                    else
                    {
                        VisitFile(child, in entry);
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
                CloudDirectories = _cloudDirectories,
                AlternateStreamCount = _streams,
                AlternateStreamBytes = _streamBytes,
                AccessDenied = _accessDenied,
                Unreadable = _unreadable,
                ReparsePointCount = _reparsePointCount,
                ReparsePoints = _reparsePoints,
                Problems = _problems,
                Listing = _lister.Used,
                StaleDirectoryEntries = _measurer.StaleEntries,
                Elapsed = _clock.Elapsed,
            };
        }

        /// <summary>Queues an ordinary or cloud folder (or walks it, for the root); records every other reparse point.</summary>
        private void VisitDirectory(string extendedPath, in DirectoryEntry entry, Stack<string>? pending)
        {
            if (entry.IsTraversableDirectory)
            {
                if (entry.ReparseKind == ReparseKind.Cloud)
                {
                    _cloudDirectories++;
                }

                if (pending is null)
                {
                    WalkTree(extendedPath);
                }
                else
                {
                    pending.Push(extendedPath);
                }
            }
            else if (entry.ReparseKind == ReparseKind.Cloud)
            {
                // A cloud folder not yet populated: nothing of it is on this disk,
                // and listing it would ask the provider to fetch its contents.
                _placeholders++;
            }
            else
            {
                // Junctions, symlinks, volume mount points, projected folders: never entered.
                RecordReparsePoint(extendedPath, in entry);
            }
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

            if (!_measurer.TryMeasure(extendedPath, in entry, _lister.VolumeSerial, out FileFacts facts, out int error))
            {
                RecordProblem(extendedPath, error);
                return;
            }

            if (_ledger.Register(in facts))
            {
                _files++;
                _bytes += facts.TotalAllocation;
                _logicalBytes += facts.Length + facts.AlternateStreamLength;
                _streams += facts.AlternateStreams;
                _streamBytes += facts.AlternateStreamAllocation;
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
