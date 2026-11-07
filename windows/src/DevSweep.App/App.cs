using System.Diagnostics;
using DevSweep.App.Background;
using DevSweep.App.Shots;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Updates;
using DevSweep.Core.Modules;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace DevSweep.App;

/// <summary>
/// The application. There is no App.xaml: the Fluent control resources and DevSweep's styles
/// are merged in code, and this class supplies the XAML type information WinUI needs.
/// </summary>
internal sealed partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _provider = new();
    private readonly RunMode _mode;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private MainWindow? _window;
    private MainViewModel? _model;
    private TrayIcon? _tray;
    private AppToasts? _toasts;
    private DispatcherQueueTimer? _tooltipTimer;
    private bool _started;
    private bool _quitting;

    public App(RunMode mode)
    {
        _mode = mode;
        AppInfo.Version = AppInfo.FromAssembly(typeof(App).Assembly);
        UnhandledException += (_, e) =>
        {
            Log.Write($"Unhandled: {e.Exception}");
            if (_mode != RunMode.Normal)
            {
                e.Handled = true;
                Program.ExitCode = 1;
                Exit();
            }
        };
    }

    public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Toolkit.Theme.Load(this);
            switch (_mode)
            {
                case RunMode.Screenshots:
                    await RunScreenshotsAsync();
                    break;
                case RunMode.Smoke:
                    await RunSmokeAsync();
                    break;
                default:
                    await RunNormalAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Failed: {ex}");
            Program.ExitCode = 1;
            Exit();
        }
    }

    private async Task RunNormalAsync()
    {
        string data = EngineBackend.DefaultDataFolder;
        var settings = new AppSettingsStore(Path.Join(data, "app-settings.json"));
        DevSweepEngine engine = EngineBackend.CreateEngine(data);

        // Toasts: registered before the window so a click on a toast that started the app is understood.
        _toasts = new AppToasts();
        _toasts.Register(WriteToastIcon(data));
        _toasts.Activated += target => ShowMainWindow(target);
        var login = new RunKeyLoginItem(new RegistryRunKeyStore(), Environment.ProcessPath ?? throw new InvalidOperationException("DevSweep's own path is unknown."));
        login.RepairIfMoved();
        var integration = new SystemIntegration(login, _toasts, Path.Join(data, "alerts.json"));

        _model = new MainViewModel(new EngineBackend(engine, settings, data), settings, integration);
        _window = new MainWindow(_model, screenshots: false) { KeepInTray = () => _model.Background.KeepInTray };
        BackgroundServices background = _model.Background;
        background.Updates.OpenRequested += (_, url) => OpenReleasePage(url);
        background.Tray.ShowRequested += (_, target) => ShowMainWindow(target);
        background.Tray.QuitRequested += (_, _) => Quit();
        background.Tray.RevealRequested += (_, path) => Toolkit.Shell.Reveal(path);

        Log.Write("start: window created, adding the tray icon");
        _tray = new TrayIcon(background.Tray);
        Log.Write($"start: tray icon added={_tray.Added}");
        background.Alerts.ActiveChanged += (_, _) => _tray?.UpdateTooltip();
        _model.Scan.Finished += (_, _) => _tray?.UpdateTooltip();
        _tooltipTimer = _dispatcher.CreateTimer();
        _tooltipTimer.Interval = TimeSpan.FromMinutes(5);
        _tooltipTimer.Tick += (_, _) => _tray?.UpdateTooltip();
        _tooltipTimer.Start();
        Program.Instance?.ListenForSecondStarts(() => _dispatcher.TryEnqueue(() => ShowMainWindow(null)));

        // Started by the login entry (--background): sit in the tray with no window. Started by a toast click: open its screen.
        ToastTarget? launchTarget = AppToasts.LaunchTarget();
        if (!Program.StartHidden || launchTarget is not null)
        {
            _window.RevealWindow();
            _started = true;
            if (launchTarget is { } target)
            {
                _model.Open(target);
            }

            await _model.StartAsync();
        }

        background.Start();
    }

    /// <summary>Shows the window (from the tray, a toast or a second start); the first show of a hidden start also begins the first scan.</summary>
    private void ShowMainWindow(ToastTarget? target)
    {
        if (_window is null || _model is null || _quitting)
        {
            return;
        }

        _window.RevealWindow();
        if (target is { } t)
        {
            _model.Open(t);
        }

        if (!_started)
        {
            _started = true;
            _ = StartModelAsync(_model);
        }
    }

    private static async Task StartModelAsync(MainViewModel model)
    {
        try
        {
            await model.StartAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write("Start: " + ex.Message);
        }
    }

    private void Quit()
    {
        _quitting = true;
        _window?.AllowClose();
        _tooltipTimer?.Stop();
        _tray?.Dispose();
        _tray = null;
        _model?.Background.Dispose();
        _toasts?.Dispose();
        _toasts = null;
        Exit();
    }

    /// <summary>Opens a release page, and only ever an https github.com address.</summary>
    private static void OpenReleasePage(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write("Browser: " + ex.Message);
        }
    }

    /// <summary>The icon the notification system shows beside DevSweep's toasts, written once next to the settings.</summary>
    private static string WriteToastIcon(string folder)
    {
        string path = Path.Join(folder, "app-icon.png");
        try
        {
            byte[] png = AppIconImage.Png(64);
            if (!File.Exists(path) || new FileInfo(path).Length != png.Length)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, png);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Toast icon: " + ex.Message);
        }

        return path;
    }

    /// <summary>
    /// <c>--smoke</c>: the real engine with the user's flags, every rule pack loaded, the main window
    /// rendered with every place built once; exits 0, or 1 on any failure. Writes what it did to
    /// stdout and <c>%TEMP%\devsweep-app-smoke.txt</c>.
    /// </summary>
    private async Task RunSmokeAsync()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        string data = Path.Join(Path.GetTempPath(), "DevSweepAppSmoke");
        DevSweepEngine engine = EngineBackend.CreateEngine(EngineBackend.DefaultDataFolder);
        Core.Rules.RuleSet rules = engine.LoadRules();
        Log.Write($"engine: {engine.Modules.Count} modules ({string.Join(", ", engine.Modules.Select(m => m.Id))}); off: {string.Join(", ", engine.Disabled)}");
        Log.Write($"rules: {rules.Rules.Count} loaded from {rules.Packs.Count} packs, {rules.Errors.Count} errors");
        if (rules.Errors.Count > 0 || rules.Rules.Count == 0)
        {
            throw new InvalidOperationException("Rule packs did not load: " + string.Join("; ", rules.Errors));
        }

        var settings = new AppSettingsStore(null);
        settings.Update(s => s with { WelcomeDone = true });
        var model = new MainViewModel(new EngineBackend(engine, settings, data), settings);
        _window = new MainWindow(model, screenshots: false);
        _window.Activate();
        await _window.WaitForRenderAsync();
        foreach (Place place in model.Places)
        {
            model.Go(place.Id);
            await _window.WaitForRenderAsync();
        }

        Log.Write($"window: rendered {model.Places.Count} places ({string.Join(", ", model.Places.Select(p => p.Id))}) in {started.Elapsed.TotalSeconds:0.0} s");
        SmokeBackgroundServices(model);
        Log.Write("smoke: ok");
        Program.ExitCode = 0;
        Exit();
    }

    /// <summary>
    /// The tray icon is created, given its tooltip and menu, and disposed; a toast is built (not shown); the login
    /// entry is read (never written). Nothing here changes the machine.
    /// </summary>
    private static void SmokeBackgroundServices(MainViewModel model)
    {
        using (var tray = new TrayIcon(model.Background.Tray))
        {
            int items = tray.BuildAndDiscardMenu();
            tray.UpdateTooltip();
            Log.Write($"tray: icon {(tray.Added ? "added to the notification area" : "created, the shell did not take it in this session")}; menu of {items} items built; tooltip \"{model.Background.Tray.Tooltip()}\"");
        }

        Log.Write("tray: ok (created and disposed)");

        string payload = AppToasts.BuildPayload(new ToastMessage("DevSweep smoke test", "Nothing is shown.", ToastTarget.CleanUp));
        if (!payload.Contains("CleanUp", StringComparison.Ordinal) || !payload.Contains("DevSweep smoke test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The toast payload is wrong: " + payload);
        }

        Log.Write("toast: payload built (" + payload.Length + " characters), target kept in its launch argument");
        string icon = Path.Join(Path.GetTempPath(), "DevSweepAppSmoke-icon.png");
        File.WriteAllBytes(icon, AppIconImage.Png(64));
        using (var toasts = new AppToasts())
        {
            bool registered = toasts.Register(icon);
            Log.Write($"toast: registered={registered}, availability={toasts.Availability} (a toast is not shown in a smoke run)");
        }

        var login = new RunKeyLoginItem(new RegistryRunKeyStore(), Environment.ProcessPath ?? "DevSweep.exe");
        Log.Write($"login: open at login is {login.State} (read only, never changed by the smoke test)");
    }

    private async Task RunScreenshotsAsync()
    {
        string folder = Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS")!;
        Directory.CreateDirectory(folder);
        Log.File = Path.Join(folder, "shots.log");
        var settings = new AppSettingsStore(null);
        settings.Update(s => s with
        {
            WelcomeDone = true,
            ProjectFolders = new List<string> { @"D:\src", @"D:\work" },
            Ignored = new[] { new IgnoredItem("chrome-profiles:Profile 3", "Chrome profile 3 (unused)") },
            Schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Monday, Hour = 9, Since = SampleData.Now },
        });
        var model = new MainViewModel(new SampleBackend(), settings, SystemIntegration.Memory(LoginState.On, new SampleToasts()));
        _window = new MainWindow(model, screenshots: true);
        _window.Activate();
        int saved = await new ScreenshotTour(_window, model, folder).RunAsync();
        Log.Write($"shots: {saved} screenshots saved in {folder}");
        Program.ExitCode = saved > 0 ? 0 : 1;
        Exit();
    }
}

/// <summary>Smoke and screenshot logs: stdout (when redirected) and a file.</summary>
internal static class Log
{
    public static string File { get; set; } = Path.Join(Path.GetTempPath(), "devsweep-app-smoke.txt");

    public static void Write(string line)
    {
        string text = $"{DateTime.UtcNow:HH:mm:ss.fff} {line}";
        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
            System.IO.File.AppendAllText(File, text + Environment.NewLine);
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
