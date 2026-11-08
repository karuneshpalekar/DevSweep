using System.Text.Json;
using System.Text.Json.Serialization;
using DevSweep.App.ViewModels.Background;
using DevSweep.Core.Health.Alerts;
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

    /// <summary>When an update check last succeeded (about one a day).</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>When one was last tried, successful or not (so a failure is not retried every minute).</summary>
    public DateTimeOffset? LastUpdateAttempt { get; init; }

    /// <summary>The version the user chose "Later" for; it is not offered again.</summary>
    public string? SkippedUpdate { get; init; }

    /// <summary>Scheduled scans (off by default; read-only like every scan).</summary>
    public ScanSchedule Schedule { get; init; } = new();

    /// <summary>Which problems raise a notification, and when.</summary>
    public AlertSettings Alerts { get; init; } = new();
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

    /// <summary>A file from an older version (or with a section set to null) gets the defaults for what it lacks.</summary>
    internal static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        // Properties are non-nullable in code but JSON can still say null; read them as the nullable values they may be.
        ScanSchedule? schedule = settings.Schedule;
        AlertSettings? alerts = settings.Alerts;
        IReadOnlyList<string>? folders = settings.ProjectFolders;
        IReadOnlyList<IgnoredItem>? ignored = settings.Ignored;
        return settings with
        {
            Schedule = schedule ?? new ScanSchedule(),
            Alerts = alerts ?? new AlertSettings(),
            ProjectFolders = folders ?? [],
            Ignored = ignored ?? [],
        };
    }

    private static AppSettings Read(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJsonContext.Default.AppSettings));
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
