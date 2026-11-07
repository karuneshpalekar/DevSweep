using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;

namespace DevSweep.App.ViewModels;

/// <summary>A module switch in Settings.</summary>
public sealed partial class ModuleToggle(ModuleInfo module, Action<string, bool> apply) : ObservableObject
{
    public ModuleInfo Module { get; } = module;

    public string Title => Module.Title;

    public bool CanChange => Module.State != ModuleState.NotInThisBuild && Module.Id is not ("locations" or "inventory");

    public string Note => Module.State == ModuleState.NotInThisBuild ? "Not in this build yet."
        : Module.Id == "inventory" ? "Needed to tell leftovers from installed apps."
        : string.Empty;

    [ObservableProperty]
    public partial bool IsOn { get; set; } = module?.IsOn ?? false;

    partial void OnIsOnChanged(bool value) => apply(Module.Id, value);
}

/// <summary>Settings: appearance, project folders, ignored items, modules, scan options, updates, about.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;
    private readonly IDevSweepBackend _backend;

    public SettingsViewModel(AppSettingsStore settings, IDevSweepBackend backend)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(backend);
        _settings = settings;
        _backend = backend;
        Appearance = settings.Current.Appearance;
        ExactScan = settings.Current.ExactScan;
        CheckTimeoutSeconds = settings.Current.CheckTimeoutSeconds;
        CheckForUpdates = settings.Current.CheckForUpdates;
        foreach (ModuleInfo module in backend.Modules.Where(m => m.Id != "locations"))
        {
            Modules.Add(new ModuleToggle(module, Toggle));
        }

        Reload();
        settings.Changed += (_, _) => Reload();
    }

    public ObservableCollection<string> ProjectFolders { get; } = [];

    public ObservableCollection<IgnoredItem> Ignored { get; } = [];

    public ObservableCollection<ModuleToggle> Modules { get; } = [];

    [ObservableProperty]
    public partial AppearanceMode Appearance { get; set; }

    [ObservableProperty]
    public partial bool ExactScan { get; set; }

    [ObservableProperty]
    public partial double CheckTimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string? UpdateStatus { get; set; }

    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    public bool UsingDefaultFolders => ProjectFolders.Count == 0;

    public static string Version => typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public string DataFolder => _backend.DataFolder;

    public static string Privacy => "DevSweep never sends anything about your PC anywhere. Scans read file and folder information only (names, sizes, dates), never the contents of your documents, and nothing is cleaned until you review and confirm. It runs as you, with no administrator rights; anything that needs one is shown as a command you run yourself.";

    partial void OnAppearanceChanged(AppearanceMode value) => _settings.Update(s => s with { Appearance = value });

    partial void OnExactScanChanged(bool value) => _settings.Update(s => s with { ExactScan = value });

    partial void OnCheckTimeoutSecondsChanged(double value)
        => _settings.Update(s => s with { CheckTimeoutSeconds = (int)Math.Clamp(double.IsNaN(value) ? 90 : value, 5, 600) });

    partial void OnCheckForUpdatesChanged(bool value) => _settings.Update(s => s with { CheckForUpdates = value });

    public void AddFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        _settings.Update(s => s with { ProjectFolders = [.. s.ProjectFolders.Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase)), full] });
    }

    public void RemoveFolder(string path) => _settings.Update(s => s with
    {
        ProjectFolders = [.. s.ProjectFolders.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))],
    });

    [RelayCommand]
    public void RestoreDefaultFolders() => _settings.Update(s => s with { ProjectFolders = [] });

    public void StopIgnoring(IgnoredItem item) => _settings.Update(s => s with { Ignored = [.. s.Ignored.Where(i => i.Id != item?.Id)] });

    [RelayCommand]
    public async Task CheckNowAsync() => UpdateStatus = await _backend.Updates.CheckAsync(CancellationToken.None);

    private void Toggle(string id, bool on)
    {
        _backend.SetModuleEnabled(id, on);
        RestartNeeded = true;
    }

    private void Reload()
    {
        ProjectFolders.Clear();
        foreach (string folder in _settings.Current.ProjectFolders)
        {
            ProjectFolders.Add(folder);
        }

        Ignored.Clear();
        foreach (IgnoredItem item in _settings.Current.Ignored)
        {
            Ignored.Add(item);
        }

        OnPropertyChanged(nameof(UsingDefaultFolders));
    }
}
