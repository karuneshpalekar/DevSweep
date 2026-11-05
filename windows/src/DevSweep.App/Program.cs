using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DevSweep.App;

/// <summary>How this run was started.</summary>
internal enum RunMode
{
    Normal,

    /// <summary><c>--smoke</c>: build the engine, load rules, render the main window, exit 0.</summary>
    Smoke,

    /// <summary><c>DEVSWEEP_SHOTS=&lt;dir&gt;</c>: walk every screen with sample data and save PNGs.</summary>
    Screenshots,
}

internal static class Program
{
    public static int ExitCode { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        RunMode mode = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase) ? RunMode.Smoke
            : Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS") is { Length: > 0 } ? RunMode.Screenshots
            : RunMode.Normal;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(mode);
        });
        return ExitCode;
    }
}
