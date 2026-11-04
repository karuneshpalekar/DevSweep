namespace DevSweep.Core.Storage;

/// <summary>
/// Writes a file so that a crash leaves either the old contents or the new,
/// never half of each: the new contents go to a temporary file beside it
/// (written through to disk), which then replaces the original with
/// <c>ReplaceFileW</c> (<see cref="File.Replace(string, string, string?, bool)"/>), or is renamed into
/// place with <c>MoveFileExW</c> when there is no original yet.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full) ?? throw new ArgumentException($"{path} has no folder.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                File.Replace(temporary, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, full, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
