using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace DevSweep.Core.Health.Common;

/// <summary>A running process and its program path (null when Windows would not say, e.g. another user's service).</summary>
public sealed record ProcessPath(int Pid, string Name, string? Path);

/// <summary>Running processes with their program paths. Faked in tests.</summary>
public interface IProcessPaths
{
    IReadOnlyList<ProcessPath> Snapshot();
}

public sealed class NoProcessPaths : IProcessPaths
{
    public static NoProcessPaths Instance { get; } = new();

    public IReadOnlyList<ProcessPath> Snapshot() => [];
}

public sealed class FixedProcessPaths(params ProcessPath[] processes) : IProcessPaths
{
    public IReadOnlyList<ProcessPath> Snapshot() => processes;
}

/// <summary>Every process, with <c>QueryFullProcessImageNameW</c> (limited query rights only).</summary>
public sealed class WindowsProcessPaths : IProcessPaths
{
    public static WindowsProcessPaths Instance { get; } = new();

    public IReadOnlyList<ProcessPath> Snapshot()
    {
        var list = new List<ProcessPath>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    list.Add(new ProcessPath(process.Id, process.ProcessName, ImagePath(process.Id)));
                }
                catch (InvalidOperationException)
                {
                    // Exited while listed.
                }
            }
        }

        return list;
    }

    /// <summary>The program path of a process, or null when it cannot be opened for a limited query.</summary>
    public static unsafe string? ImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 4)
        {
            return null;
        }

        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull)
        {
            return null;
        }

        try
        {
            char* buffer = stackalloc char[32768];
            uint size = 32768;
            return PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }
}
