using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DevSweep.Core.Health.Ports;

public enum StopOutcome
{
    /// <summary>It exited after being asked.</summary>
    Stopped,

    /// <summary>It was asked but is still running: ending it needs the user's confirmation.</summary>
    StillRunning,

    /// <summary>Another user (or Windows) owns it, or its owner could not be read.</summary>
    NotYours,

    /// <summary>A process DevSweep never stops (Windows itself, the shell, DevSweep).</summary>
    NotAllowed,

    /// <summary>No such process (it already exited).</summary>
    NotFound,

    /// <summary>It could not be asked (no window and no console to send Ctrl+C to).</summary>
    CouldNotAsk,

    /// <summary>Ending it failed.</summary>
    Failed,
}

public sealed record StopResult(StopOutcome Outcome, string Message, string? Method = null);

/// <summary>
/// Stops a process the way its user would: first politely (close its window, or Ctrl+C in its
/// console, the same as pressing it in its terminal), and only when it is still running and the
/// user confirms, by ending it (<c>TerminateProcess</c>). Only processes running as the current
/// user; never Windows' own processes, Explorer or DevSweep.
/// </summary>
public sealed class ProcessStopper(IProcessDetails details, Func<int, bool>? sendCtrlC = null)
{
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "fontdrvhost", "sihost",
        "ctfmon", "conhost", "OpenConsole", "WindowsTerminal", "devsweep", "DevSweep", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost",
    };

    private readonly Func<int, bool> _sendCtrlC = sendCtrlC ?? ConsoleInterrupt.Send;

    /// <summary>Why DevSweep would not stop this process, or null when it may.</summary>
    public StopResult? Refusal(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId)
        {
            return new StopResult(StopOutcome.NotAllowed, "DevSweep doesn't stop Windows itself or itself.");
        }

        ProcessInfo? info = details.Read(pid);
        if (info is null)
        {
            return new StopResult(StopOutcome.NotFound, "That process has already exited.");
        }

        if (Never.Contains(info.Name))
        {
            return new StopResult(StopOutcome.NotAllowed, $"{info.Name} is part of Windows (or DevSweep); DevSweep never stops it.");
        }

        return info.OwnedByYou == true ? null
            : new StopResult(StopOutcome.NotYours, info.OwnedByYou is null
                ? $"Windows wouldn't say who runs {info.Name}, so DevSweep leaves it alone."
                : $"{info.Name} runs as another user or as a Windows service, so DevSweep leaves it alone.");
    }

    /// <summary>Asks the process to stop (close its window, or Ctrl+C) and waits up to <paramref name="wait"/>.</summary>
    public StopResult AskToStop(int pid, TimeSpan wait)
    {
        if (Refusal(pid) is { } refusal)
        {
            return refusal;
        }

        using Process? process = Open(pid);
        if (process is null)
        {
            return new StopResult(StopOutcome.NotFound, "That process has already exited.");
        }

        string method;
        if (process.MainWindowHandle != IntPtr.Zero)
        {
            PInvoke.PostMessage((HWND)process.MainWindowHandle, PInvoke.WM_CLOSE, default, default);
            method = "asked its window to close";
        }
        else if (_sendCtrlC(pid))
        {
            method = "sent Ctrl+C to its console";
        }
        else
        {
            return new StopResult(StopOutcome.CouldNotAsk, $"{process.ProcessName} has no window and no console DevSweep could send Ctrl+C to.");
        }

        return process.WaitForExit(wait)
            ? new StopResult(StopOutcome.Stopped, $"{process.ProcessName} stopped.", method)
            : new StopResult(StopOutcome.StillRunning, $"{process.ProcessName} is still running after DevSweep {method}. Ending it may lose unsaved work.", method);
    }

    /// <summary>Ends the process. Call only after the user confirmed (after <see cref="AskToStop"/> said it is still running).</summary>
    public StopResult End(int pid)
    {
        if (Refusal(pid) is { } refusal)
        {
            return refusal;
        }

        using Process? process = Open(pid);
        if (process is null)
        {
            return new StopResult(StopOutcome.NotFound, "That process has already exited.");
        }

        try
        {
            process.Kill(entireProcessTree: false);
            process.WaitForExit(TimeSpan.FromSeconds(5));
            return new StopResult(StopOutcome.Stopped, $"{process.ProcessName} was ended.", "ended (TerminateProcess)");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new StopResult(StopOutcome.Failed, $"{process.ProcessName} could not be ended: {ex.Message}");
        }
    }

    private static Process? Open(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Sends Ctrl+C to a console process: attach to its console, ignore Ctrl+C here, raise it for the
/// console, detach. Everything attached to that console gets it, exactly as when the user presses
/// Ctrl+C in that window. The calling process loses its own console, so a console program (the CLI)
/// does this from a short-lived helper process; a windowed app can call it directly.
/// </summary>
public static class ConsoleInterrupt
{
    private const uint CtrlCEvent = 0;

    public static bool Send(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        PInvoke.FreeConsole();
        if (!PInvoke.AttachConsole((uint)pid))
        {
            return false;
        }

        try
        {
            PInvoke.SetConsoleCtrlHandler(null, true);
            bool sent = PInvoke.GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            Thread.Sleep(250);
            return sent;
        }
        finally
        {
            PInvoke.FreeConsole();
            PInvoke.SetConsoleCtrlHandler(null, false);
        }
    }
}
