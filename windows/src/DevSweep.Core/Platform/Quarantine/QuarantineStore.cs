using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DevSweep.Core.Safety;
using DevSweep.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace DevSweep.Core.Platform.Quarantine;

/// <summary>
/// DevSweep's own undo for items the Recycle Bin would not keep (too big, path
/// too long, no bin, unknown limit). An item is renamed by handle into a
/// quarantine folder on its own volume: instant, no copy, and reversible.
/// </summary>
public sealed class QuarantineStore(IQuarantineLocations locations)
{
    private const string ManifestExtension = ".json";

    public QuarantineStore()
        : this(new DefaultQuarantineLocations())
    {
    }

    public IQuarantineLocations Locations { get; } = locations ?? throw new ArgumentNullException(nameof(locations));

    /// <summary>
    /// Moves a checked item into the quarantine on its volume. <paramref name="size"/>
    /// is its measurement (kept for the listing). Fails, changing nothing, when the
    /// volume has no quarantine or the rename would cross volumes.
    /// </summary>
    public QuarantineResult Move(VerifiedItem item, WalkResult size, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(size);

        string? root = Locations.RootFor(item.Path, out string why);
        if (root is null)
        {
            return new QuarantineResult(false, null, why);
        }

        if (Volumes.ForPath(root).SerialNumber != Volumes.ForPath(item.Path).SerialNumber)
        {
            return new QuarantineResult(false, null,
                $"The quarantine folder {root} is on another volume; DevSweep never copies an item across volumes.");
        }

        string id = NewId(DateTimeOffset.UtcNow);
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        var entry = new QuarantineEntry
        {
            Id = id,
            OriginalPath = item.Path,
            StoredPath = Path.Combine(folder, Path.GetFileName(item.Path)),
            IsDirectory = item.IsDirectory,
            Bytes = size.Bytes,
            LogicalBytes = size.LogicalBytes,
            QuarantinedAt = DateTimeOffset.UtcNow,
            Reason = reason,
        };

        // The manifest goes first, so even a crash right after the move knows where the item came from.
        WriteManifest(root, entry);
        if (!NativeFile.RenameByHandle(item.Handle, LongPath.ToExtended(entry.StoredPath), out int error))
        {
            File.Delete(ManifestPath(root, id));
            Directory.Delete(folder);
            string message = error == Win32Errors.NotSameDevice
                ? "The quarantine is on another volume; DevSweep never copies an item across volumes."
                : string.Create(CultureInfo.InvariantCulture, $"{item.Path} could not be moved into the quarantine (Win32 error {error}).");
            return new QuarantineResult(false, null, message);
        }

        return new QuarantineResult(true, entry, $"Moved to DevSweep's quarantine ({entry.StoredPath}).");
    }

    /// <summary>Everything in every quarantine folder, newest first.</summary>
    public IReadOnlyList<QuarantineEntry> List()
    {
        var entries = new List<QuarantineEntry>();
        foreach (string root in Locations.ExistingRoots())
        {
            foreach (string manifest in Directory.EnumerateFiles(root, "*" + ManifestExtension))
            {
                if (ReadManifest(manifest) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return [.. entries.OrderByDescending(e => e.QuarantinedAt)];
    }

    public QuarantineEntry? Find(string id)
        => List().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts an item back exactly where it was: recreates missing parent folders,
    /// refuses if anything now exists there or the way there goes through a link,
    /// and renames it back (same volume, never a copy).
    /// </summary>
    public QuarantineRestoreResult Restore(string id)
    {
        if (Find(id) is not { } entry)
        {
            return new QuarantineRestoreResult(false, id, $"Nothing with ID {id} is in the quarantine.");
        }

        string original = entry.OriginalPath;
        if (!SafePath.TryCanonicalize(original, out _, out string rejection))
        {
            return new QuarantineRestoreResult(false, original, rejection);
        }

        if (Exists(original))
        {
            return new QuarantineRestoreResult(false, original, $"Something now exists at {original}, so the item was left in the quarantine.");
        }

        string? parent = Path.GetDirectoryName(original);
        if (parent is not null)
        {
            Directory.CreateDirectory(LongPath.ToExtended(parent));
        }

        if (ReparseGuard.FindReparseAncestor(original) is { } link)
        {
            return new QuarantineRestoreResult(false, original, $"The way to {original} now goes through a junction or link ({link}), so the item was left in the quarantine.");
        }

        using SafeFileHandle? handle = NativeFile.Open(LongPath.ToExtended(entry.StoredPath), NativeFile.DeleteAccess, NativeFile.NoFollow, out int error);
        if (handle is null)
        {
            return new QuarantineRestoreResult(false, original, string.Create(CultureInfo.InvariantCulture, $"The quarantined item could not be opened (Win32 error {error})."));
        }

        if (!NativeFile.RenameByHandle(handle, LongPath.ToExtended(original), out error))
        {
            string message = Win32Errors.IsExists(error)
                ? $"Something now exists at {original}, so the item was left in the quarantine."
                : string.Create(CultureInfo.InvariantCulture, $"The item could not be moved back (Win32 error {error}).");
            return new QuarantineRestoreResult(false, original, message);
        }

        handle.Dispose();
        Forget(entry);
        return new QuarantineRestoreResult(true, original, $"Restored to {original}.");
    }

    /// <summary>Deletes one quarantined item for good.</summary>
    public DeleteReport Delete(string id)
    {
        if (Find(id) is not { } entry)
        {
            return new DeleteReport(0, 0, [new WalkProblem(id, Win32Errors.FileNotFound)]);
        }

        DeleteReport report = new(0, 0, []);
        using (SafeFileHandle? handle = NativeFile.Open(LongPath.ToExtended(entry.StoredPath), NativeFile.DeleteAccess, NativeFile.NoFollow, out int error))
        {
            if (handle is not null)
            {
                bool isFolder = entry.IsDirectory
                    && FindFileEnumerator.TryReadEntry(LongPath.ToExtended(entry.StoredPath), out DirectoryEntry stored, out _)
                    && stored.IsTraversableDirectory;
                report = TreeDeleter.DeleteItem(handle, entry.StoredPath, isFolder);
            }
            else if (!Win32Errors.IsNotFound(error))
            {
                return new DeleteReport(0, 0, [new WalkProblem(entry.StoredPath, error)]);
            }
        }

        if (report.Succeeded)
        {
            Forget(entry);
        }

        return report;
    }

    /// <summary>Deletes for good every quarantined item older than <paramref name="olderThan"/> (all of them when null).</summary>
    public IReadOnlyList<(QuarantineEntry Entry, DeleteReport Report)> Empty(TimeSpan? olderThan = null, DateTimeOffset? now = null)
    {
        DateTimeOffset at = now ?? DateTimeOffset.UtcNow;
        return [.. List()
            .Where(e => olderThan is null || e.AgeAt(at) >= olderThan)
            .Select(e => (e, Delete(e.Id)))];
    }

    internal static string NewId(DateTimeOffset at)
        => string.Create(CultureInfo.InvariantCulture, $"{at:yyyyMMdd-HHmmss}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}");

    private static bool Exists(string path)
        => FindFileEnumerator.TryReadEntry(LongPath.ToExtended(path), out _, out int error) || !Win32Errors.IsNotFound(error);

    private static string ManifestPath(string root, string id) => Path.Combine(root, id + ManifestExtension);

    private static void WriteManifest(string root, QuarantineEntry entry)
        => AtomicFile.Write(ManifestPath(root, entry.Id), JsonSerializer.SerializeToUtf8Bytes(entry, CoreJsonContext.Default.QuarantineEntry));

    private static QuarantineEntry? ReadManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), CoreJsonContext.Default.QuarantineEntry);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Removes an entry's (now empty) folder and its manifest.</summary>
    private static void Forget(QuarantineEntry entry)
    {
        string folder = Path.GetDirectoryName(entry.StoredPath)!;
        string root = Path.GetDirectoryName(folder)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }

        File.Delete(ManifestPath(root, entry.Id));
    }
}
