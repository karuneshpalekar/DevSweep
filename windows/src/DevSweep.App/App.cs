using DevSweep.App.Shots;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Modules;
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
    private MainWindow? _window;

    public App(RunMode mode)
    {
        _mode = mode;
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
        catch (Exception ex) when (_mode != RunMode.Normal)
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
        var model = new MainViewModel(new EngineBackend(engine, settings, data), settings);
        _window = new MainWindow(model, screenshots: false);
        _window.Activate();
        await model.StartAsync();
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
        Log.Write("smoke: ok");
        Program.ExitCode = 0;
        Exit();
    }

    private async Task RunScreenshotsAsync()
    {
        string folder = Environment.GetEnvironmentVariable("DEVSWEEP_SHOTS")!;
        Directory.CreateDirectory(folder);
        Log.File = Path.Join(folder, "shots.log");
        var settings = new AppSettingsStore(null);
        settings.Update(s => s with { WelcomeDone = true, ProjectFolders = new List<string> { @"D:\src", @"D:\work" }, Ignored = new[] { new IgnoredItem("chrome-profiles:Profile 3", "Chrome profile 3 (unused)") } });
        var model = new MainViewModel(new SampleBackend(), settings);
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
