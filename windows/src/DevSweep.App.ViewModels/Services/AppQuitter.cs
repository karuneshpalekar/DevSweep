using System.Diagnostics;

namespace DevSweep.App.ViewModels.Services;

/// <summary>How asking an app to quit ended.</summary>
public enum QuitOutcome
{
    /// <summary>It was not running (any more).</summary>
    NotRunning,

    /// <summary>It closed after being asked.</summary>
    Closed,

    /// <summary>It was asked and is still running (an unsaved-work prompt, a background process, or it declined). Nothing was forced.</summary>
    StillRunning,

    /// <summary>It could not be asked: it has no window to send the request to.</summary>
    NoWindow,
}

/// <summary>The result of asking an app to quit, with a sentence for the screen.</summary>
public sealed record QuitResult(QuitOutcome Outcome, string Message)
{
    public bool Done => Outcome is QuitOutcome.NotRunning or QuitOutcome.Closed;
}

/// <summary>
/// Asks a running app to close, the way its user would by clicking the window's X (a close request to its main windows).
/// It never ends a process: an app with unsaved work shows its own prompt, and one that declines stays running.
/// </summary>
public interface IAppQuitter
{
    /// <summary>True while a process with this name (the rule's blocker name, "chrome", with or without ".exe") runs.</summary>
    bool IsRunning(string processName);

    /// <summary>Asks every window of the app to close and waits briefly for it to exit.</summary>
    Task<QuitResult> QuitAsync(string processName, CancellationToken cancellationToken = default);
}

/// <summary>The real thing: <see cref="Process.CloseMainWindow"/> on each process with that name, repeated for the windows behind the first.</summary>
public sealed class ProcessQuitter : IAppQuitter
{
    private const int Rounds = 6;
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(1500);

    public static string Bare(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public bool IsRunning(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        Process[] processes = Process.GetProcessesByName(Bare(processName));
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<QuitResult> QuitAsync(string processName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processName);
        string bare = Bare(processName);
        bool asked = false;
        for (int round = 0; round < Rounds; round++)
        {
            Process[] processes = Process.GetProcessesByName(bare);
            if (processes.Length == 0)
            {
                return asked ? new QuitResult(QuitOutcome.Closed, $"{bare} closed.") : new QuitResult(QuitOutcome.NotRunning, $"{bare} isn't running.");
            }

            bool sent = false;
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.MainWindowHandle != 0 && process.CloseMainWindow())
                        {
                            sent = true;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // It exited between listing and asking.
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }

            if (!sent)
            {
                return asked
                    ? new QuitResult(QuitOutcome.StillRunning, $"{bare} is still running. It may be waiting for you to answer a prompt, or running in the background.")
                    : new QuitResult(QuitOutcome.NoWindow, $"{bare} has no window to ask. Close it from the tray or Task Manager yourself.");
            }

            asked = true;
            for (int waited = 0; waited < Patience.TotalMilliseconds && IsRunning(bare); waited += 100)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            }
        }

        return IsRunning(bare)
            ? new QuitResult(QuitOutcome.StillRunning, $"{bare} is still running. It may be waiting for you to answer a prompt, or running in the background.")
            : new QuitResult(QuitOutcome.Closed, $"{bare} closed.");
    }
}
