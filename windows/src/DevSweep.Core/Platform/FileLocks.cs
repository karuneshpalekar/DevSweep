using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RestartManager;

namespace DevSweep.Core.Platform;

/// <summary>A process that has one of an item's files open.</summary>
/// <param name="ProcessId">Process ID.</param>
/// <param name="Name">What Restart Manager calls it (the app's display name), or the executable name.</param>
/// <param name="ExecutableName">The process image name, when it could be read.</param>
/// <param name="ServiceName">The service's short name, for services.</param>
public sealed record LockingProcess(int ProcessId, string Name, string? ExecutableName, string? ServiceName)
{
    public override string ToString()
    {
        string exe = ExecutableName is { Length: > 0 } e && !string.Equals(e, Name, StringComparison.OrdinalIgnoreCase) ? $"{e}, " : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{Name} ({exe}PID {ProcessId})");
    }
}

/// <summary>Who holds an item's files open.</summary>
/// <param name="Processes">The processes, each once.</param>
/// <param name="FilesChecked">Files registered with Restart Manager.</param>
/// <param name="Truncated">True when the item had more files than were checked.</param>
/// <param name="FilesUnchecked">
/// Files Restart Manager cannot take: paths of 260 characters or more with no
/// short form under that length. A process holding one makes the removal fail
/// cleanly instead (a rename or delete of an open file is refused by Windows).
/// </param>
public sealed record LockReport(IReadOnlyList<LockingProcess> Processes, int FilesChecked, bool Truncated, int FilesUnchecked = 0)
{
    /// <summary>
    /// Set when Restart Manager could not answer (it returns ERROR_ACCESS_DENIED for a
    /// standard user in a non-interactive logon, for example): the files were then
    /// probed by opening each for delete, which finds a file in use but not who holds it.
    /// </summary>
    public string? RestartManagerError { get; init; }

    /// <summary>A file found in use by the probe (when Restart Manager could not name the process).</summary>
    public string? InUseFile { get; init; }

    public bool IsLocked => Processes.Count > 0 || InUseFile is not null;

    /// <summary>"Close X first." for the user, or null when nothing holds the files.</summary>
    public string? BlockerMessage => Processes.Count > 0
        ? $"Close {string.Join(", ", Processes.Select(p => p.ToString()))} first; it has files in this item open."
        : InUseFile is { } file
            ? $"Close the program using {file} first (Windows did not say which program it is)."
            : null;
}

/// <summary>
/// Names the processes holding any file of an item open, with the Restart
/// Manager (<c>RmStartSession</c>, <c>RmRegisterResources</c>, <c>RmGetList</c>),
/// so a removal can say "Close X first" instead of failing half way.
/// </summary>
public static class FileLocks
{
    /// <summary>Most files registered per item; bigger items are checked in part (and the report says so).</summary>
    public const int DefaultFileLimit = 20_000;

    private const int Batch = 500;
    private const int MaxRmPath = 260;

    /// <summary>
    /// Checks every file in <paramref name="path"/> (a file or a folder), up to
    /// <paramref name="fileLimit"/> files. When Restart Manager cannot answer, every
    /// file is probed instead (see <see cref="LockReport.RestartManagerError"/>).
    /// </summary>
    public static LockReport Check(string path, int fileLimit = DefaultFileLimit)
    {
        IReadOnlyList<string> files = ItemFiles.Collect(path, fileLimit, out bool truncated);
        List<string> usable = [.. files.Select(RmPath).OfType<string>()];
        try
        {
            return new LockReport(Find(usable), usable.Count, truncated, files.Count - usable.Count);
        }
        catch (Win32Exception ex)
        {
            return new LockReport([], 0, truncated, files.Count)
            {
                RestartManagerError = ex.Message,
                InUseFile = FirstInUse(files),
            };
        }
    }

    /// <summary>
    /// The first file another process holds open without letting it be deleted:
    /// opening it for DELETE (sharing everything) then fails with a sharing violation.
    /// </summary>
    private static string? FirstInUse(IReadOnlyList<string> files)
    {
        foreach (string file in files)
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle? handle = NativeFile.Open(LongPath.ToExtended(file), NativeFile.DeleteAccess, NativeFile.NoFollow, out int error);
            if (handle is null && error == Win32Errors.SharingViolation)
            {
                return LongPath.StripPrefix(file);
            }
        }

        return null;
    }

    /// <summary>The processes holding any of <paramref name="files"/> open.</summary>
    /// <exception cref="Win32Exception">Restart Manager failed.</exception>
    public static IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return [];
        }

        Span<char> key = stackalloc char[(int)PInvoke.CCH_RM_SESSION_KEY + 1];
        Throw(PInvoke.RmStartSession(out uint session, key), "RmStartSession");
        try
        {
            for (int i = 0; i < files.Count; i += Batch)
            {
                string[] batch = [.. files.Skip(i).Take(Batch)];
                Throw(PInvoke.RmRegisterResources(session, batch, default, default), "RmRegisterResources");
            }

            return List(session);
        }
        finally
        {
            PInvoke.RmEndSession(session);
        }
    }

    private static List<LockingProcess> List(uint session)
    {
        uint count = 0;
        RM_PROCESS_INFO[] buffer = [];
        while (true)
        {
            WIN32_ERROR result = PInvoke.RmGetList(session, out uint needed, ref count, buffer, out _);
            if (result == WIN32_ERROR.ERROR_SUCCESS)
            {
                break;
            }

            if (result != WIN32_ERROR.ERROR_MORE_DATA)
            {
                Throw(result, "RmGetList");
            }

            buffer = new RM_PROCESS_INFO[needed + 4];
            count = (uint)buffer.Length;
        }

        var processes = new List<LockingProcess>((int)count);
        foreach (RM_PROCESS_INFO info in buffer.AsSpan(0, (int)count))
        {
            int pid = (int)info.Process.dwProcessId;

            // DevSweep itself holds the item it checked open (for removal through that handle).
            if (pid == Environment.ProcessId || processes.Exists(p => p.ProcessId == pid))
            {
                continue;
            }

            string appName = info.strAppName.ToString();
            string? service = info.strServiceShortName.ToString() is { Length: > 0 } s ? s : null;
            string? exe = ExecutableName(pid);
            processes.Add(new LockingProcess(pid, appName.Length > 0 ? appName : exe ?? "a process", exe, service));
        }

        return processes;
    }

    private static string? ExecutableName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            return null; // Exited meanwhile.
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Restart Manager takes paths shorter than MAX_PATH only (it fails with
    /// ERROR_WRITE_FAULT otherwise): a long path is replaced by its 8.3 short form
    /// when that is short enough, otherwise it cannot be checked (null).
    /// </summary>
    private static string? RmPath(string path)
    {
        string display = LongPath.StripPrefix(path);
        if (display.Length < MaxRmPath)
        {
            return display;
        }

        Span<char> buffer = new char[display.Length + 8];
        uint length = PInvoke.GetShortPathName(LongPath.ToExtended(display), buffer);
        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        string shortForm = LongPath.StripPrefix(buffer[..(int)length].ToString());
        return shortForm.Length < MaxRmPath ? shortForm : null;
    }

    private static void Throw(WIN32_ERROR result, string call)
    {
        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception((int)result, $"{call} failed ({(int)result}).");
        }
    }
}

/// <summary>The files of an item (a file, or every file in a folder), never following links.</summary>
public static class ItemFiles
{
    public static IReadOnlyList<string> Collect(string path, int limit, out bool truncated)
    {
        string extended = LongPath.ToExtended(path);
        truncated = false;
        if (!FindFileEnumerator.TryReadEntry(extended, out DirectoryEntry root, out _))
        {
            return [];
        }

        if (!root.IsTraversableDirectory)
        {
            return root.IsDirectory ? [] : [extended];
        }

        var files = new List<string>();
        var lister = new DirectoryLister(ListingMethod.Auto);
        var pending = new Stack<string>();
        pending.Push(extended);
        while (pending.TryPop(out string? directory))
        {
            using IDirectoryListing? listing = lister.Open(directory).Listing;
            while (listing is not null && listing.MoveNext(out DirectoryEntry entry))
            {
                string child = LongPath.Join(directory, entry.Name);
                if (entry.IsTraversableDirectory)
                {
                    pending.Push(child);
                }
                else if (!entry.IsDirectory && entry.CloudState != CloudFileState.Placeholder)
                {
                    if (files.Count >= limit)
                    {
                        truncated = true;
                        return files;
                    }

                    files.Add(child);
                }
            }
        }

        return files;
    }
}
