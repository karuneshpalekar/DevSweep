using DevSweep.App.Background;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DevSweep.App;

/// <summary>How this run was started.</summary>
internal enum RunMode
{
    Normal,

    /// <summary><c>--smoke</c>: build the engine, load rules, render the main window, create and dispose the tray icon, exit 0.</summary>
    Smoke,

    /// <summary><c>DEVSWEEP_SHOTS=&lt;dir&gt;</c>: walk every screen with sample data and save PNGs.</summary>
    Screenshots,
}

internal static class Program
{
    public static int ExitCode { get; set; }

    /// <summary><c>--background</c> (what the login entry passes): start in the tray with no window.</summary>
    public static bool StartHidden { get; private set; }

    /// <summary>Set for a normal run; a second start signals it and exits.</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        RunMode mode = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase) ? RunMode.Smoke
            : Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS") is { Length: > 0 } ? RunMode.Screenshots
            : RunMode.Normal;
        StartHidden = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (mode == RunMode.Normal)
        {
            Instance = SingleInstance.Acquire();
            if (!Instance.IsFirst)
            {
                // DevSweep is already running (probably in the tray): ask it to show its window.
                Instance.SignalFirst();
                Instance.Dispose();
                return 0;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App(mode);
            GC.KeepAlive(app);
        });
        Instance?.Dispose();
        return ExitCode;
    }
}
