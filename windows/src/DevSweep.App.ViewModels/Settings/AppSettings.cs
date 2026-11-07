using System.Text.Json;
using System.Text.Json.Serialization;
using DevSweep.Core.Storage;

namespace DevSweep.App.ViewModels.Settings;

/// <summary>Light, dark, or whatever Windows uses.</summary>
public enum AppearanceMode
{
    System,
    Light,
    Dark,
}

/// <summary>An item the user chose to always ignore.</summary>
public sealed record IgnoredItem(string Id, string Title);

/// <summary>What <c>%LOCALAPPDATA%\DevSweep\app-settings.json</c> holds.</summary>
public sealed record AppSettings
{
    public AppearanceMode Appearance { get; init; } = AppearanceMode.System;

    public bool WelcomeDone { get; init; }

    /// <summary>Where to look for idle projects; empty means the rules' own folders.</summary>
    public IReadOnlyList<string> ProjectFolders { get; init; } = [];

    public IReadOnlyList<IgnoredItem> Ignored { get; init; } = [];

    /// <summary>Count alternate data streams during the scan (slower). Review always measures exactly.</summary>
    public bool ExactScan { get; init; }

    /// <summary>A check running longer than this is skipped and reported.</summary>
    public int CheckTimeoutSeconds { get; init; } = 90;

    public bool CheckForUpdates { get; init; } = true;
}

/// <summary>Reads and writes <see cref="AppSettings"/> (source-generated JSON, atomic writes).</summary>
public sealed class AppSettingsStore
{
    private readonly string? _path;

    /// <param name="path">The file; null keeps settings in memory only (screenshots, tests).</param>
    public AppSettingsStore(string? path)
    {
        _path = path;
        Current = Read(path);
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current);
        if (_path is not null)
        {
            AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(Current, AppJsonContext.Default.AppSettings));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Read(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
