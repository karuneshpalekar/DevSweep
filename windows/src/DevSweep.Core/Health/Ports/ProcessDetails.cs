using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevSweep.Core.Health.Common;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace DevSweep.Core.Health.Ports;

/// <summary>What DevSweep could learn about a process. Anything Windows would not say is null.</summary>
public sealed record ProcessInfo
{
    public required int Pid { get; init; }

    /// <summary>Process name without ".exe".</summary>
    public required string Name { get; init; }

    public string? Path { get; init; }

    public string? CommandLine { get; init; }

    /// <summary>The working directory, read from the process's own parameters (same-bitness processes you own).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Runs as the current user; null when the owner could not be read.</summary>
    public bool? OwnedByYou { get; init; }

    public DateTimeOffset? Started { get; init; }

    /// <summary>Has a main window (it can be asked to close like clicking X).</summary>
    public bool HasWindow { get; init; }
}

/// <summary>Reads process details. Faked in tests.</summary>
public interface IProcessDetails
{
    ProcessInfo? Read(int pid);
}

public sealed class FixedProcessDetails : IProcessDetails
{
    public Dictionary<int, ProcessInfo> Processes { get; } = [];

    public ProcessInfo? Read(int pid) => Processes.GetValueOrDefault(pid);
}

/// <summary>
/// The real processes: program path (<c>QueryFullProcessImageNameW</c>), command line
/// (<c>NtQueryInformationProcess(ProcessCommandLineInformation)</c>, Windows 8.1 and later, limited
/// query rights), owner (token user SID compared with ours) and working directory. The working
/// directory is not exposed by any documented API: it is read from the process's
/// <c>RTL_USER_PROCESS_PARAMETERS</c> through its PEB (the offsets of x64 Windows, the same method
/// Process Explorer and psutil use), only for 64-bit processes that can be opened for reading; it is
/// marked as such wherever it is used.
/// </summary>
public sealed unsafe class WindowsProcessDetails : IProcessDetails
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int PebProcessParameters = 0x20;
    private const int ParametersCurrentDirectory = 0x38;

    public static WindowsProcessDetails Instance { get; } = new();

    public ProcessInfo? Read(int pid)
    {
        string name;
        bool hasWindow = false;
        DateTimeOffset? started = null;
        try
        {
            using Process process = Process.GetProcessById(pid);
            name = process.ProcessName;
            try
            {
                hasWindow = process.MainWindowHandle != IntPtr.Zero;
                started = process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Not ours to read.
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        var info = new ProcessInfo { Pid = pid, Name = name, Path = WindowsProcessPaths.ImagePath(pid), HasWindow = hasWindow, Started = started };
        if (pid <= 4)
        {
            return info;
        }

        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid);
        if (handle.IsNull)
        {
            handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        }

        if (handle.IsNull)
        {
            return info with { OwnedByYou = null };
        }

        using var safe = new SafeProcessHandle(handle, ownsHandle: true);
        return info with
        {
            CommandLine = CommandLine(safe),
            WorkingDirectory = WorkingDirectory(safe),
            OwnedByYou = IsCurrentUser(safe),
        };
    }

    /// <summary>True when the process's token user is the current user (null when the token cannot be read).</summary>
    public static bool? IsCurrentUser(SafeHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        byte[]? theirs = TokenUser(process);
        using Process current = Process.GetCurrentProcess();
        byte[]? ours = TokenUser(current.SafeHandle);
        if (theirs is null || ours is null)
        {
            return null;
        }

        fixed (byte* a = theirs)
        fixed (byte* b = ours)
        {
            return PInvoke.EqualSid(((TOKEN_USER*)a)->User.Sid, ((TOKEN_USER*)b)->User.Sid);
        }
    }

    private static byte[]? TokenUser(SafeHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            return null;
        }

        using (token)
        {
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, out uint needed);
            if (needed == 0 || needed > 4096)
            {
                return null;
            }

            byte[] buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                return PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, p, needed, out _) ? buffer : null;
            }
        }
    }

    private static string? CommandLine(SafeHandle process)
    {
        uint length = 0;
        Windows.Wdk.PInvoke.NtQueryInformationProcess(process, (Windows.Wdk.System.Threading.PROCESSINFOCLASS)ProcessCommandLineInformation, null, 0, ref length);
        if (length < 16 || length > 1024 * 1024)
        {
            return null;
        }

        byte[] buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (Windows.Wdk.PInvoke.NtQueryInformationProcess(process, (Windows.Wdk.System.Threading.PROCESSINFOCLASS)ProcessCommandLineInformation, p, length, ref length).SeverityCode != NTSTATUS.Severity.Success)
            {
                return null;
            }

            ushort bytes = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            nint text = (nint)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
            long offset = text - (nint)p;
            return offset >= 16 && offset + bytes <= length ? new string((char*)(p + offset), 0, bytes / 2) : null;
        }
    }

    private static string? WorkingDirectory(SafeHandle process)
    {
        if (!PInvoke.IsWow64Process(process, out BOOL wow64) || wow64)
        {
            return null;
        }

        byte[] basic = new byte[48];
        uint returned = 0;
        fixed (byte* b = basic)
        {
            if (Windows.Wdk.PInvoke.NtQueryInformationProcess(process, (Windows.Wdk.System.Threading.PROCESSINFOCLASS)ProcessBasicInformation, b, 48, ref returned).SeverityCode != NTSTATUS.Severity.Success)
            {
                return null;
            }
        }

        ulong peb = BinaryPrimitives.ReadUInt64LittleEndian(basic.AsSpan(8));
        if (peb == 0 || Read(process, peb + PebProcessParameters, 8) is not { } parametersBytes)
        {
            return null;
        }

        ulong parameters = BinaryPrimitives.ReadUInt64LittleEndian(parametersBytes);
        if (parameters == 0 || Read(process, parameters + ParametersCurrentDirectory, 16) is not { } unicode)
        {
            return null;
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(unicode);
        ulong buffer = BinaryPrimitives.ReadUInt64LittleEndian(unicode.AsSpan(8));
        if (length == 0 || length > 65534 || buffer == 0 || Read(process, buffer, length) is not { } text)
        {
            return null;
        }

        string directory = System.Text.Encoding.Unicode.GetString(text);
        return System.IO.Path.IsPathFullyQualified(directory) ? directory : null;
    }

    private static byte[]? Read(SafeHandle process, ulong address, int count)
    {
        byte[] buffer = new byte[count];
        return PInvoke.ReadProcessMemory(process, (void*)address, buffer, out nuint read) && (int)read == count ? buffer : null;
    }
}
