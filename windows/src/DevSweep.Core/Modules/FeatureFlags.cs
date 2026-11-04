using System.Text.Json;
using DevSweep.Core.Storage;

namespace DevSweep.Core.Modules;

/// <summary>What <c>%LOCALAPPDATA%\DevSweep\features.json</c> holds.</summary>
public sealed record FeatureSettings
{
    /// <summary>Flags turned off, e.g. <c>"category:leftovers"</c>.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = [];

    /// <summary>Flags turned on that are off by default (experimental modules).</summary>
    public IReadOnlyList<string> Enabled { get; init; } = [];
}

/// <summary>
/// Turns parts of the engine on and off (PLAN 7.6: each place is a module behind a
/// flag, so v1's scope is decided by quality). Flags are <c>kind:name</c>:
/// <list type="bullet">
/// <item><c>module:&lt;id&gt;</c> (<c>cleanup</c>, <c>inventory</c>; M3 adds <c>health</c>, <c>projects</c>);</item>
/// <item><c>detector:&lt;kind&gt;</c> (<c>orphanedAppData</c>...): its rules are skipped;</item>
/// <item><c>category:&lt;yamlName&gt;</c> (<c>leftovers</c>...): rules in it are skipped;</item>
/// <item><c>inventory:&lt;Source&gt;</c> (<c>Winget</c>...): the source is not read, so nothing is ever called a leftover;</item>
/// <item><c>rule:&lt;id&gt;</c>: one rule is skipped.</item>
/// </list>
/// Everything is on unless turned off; a module may be off by default (experimental) until turned on.
/// Names compare case-insensitively.
/// </summary>
public sealed class FeatureFlags
{
    private readonly HashSet<string> _disabled;
    private readonly HashSet<string> _enabled;

    public FeatureFlags(IEnumerable<string>? disabled = null, IEnumerable<string>? enabled = null)
    {
        _disabled = new HashSet<string>(disabled ?? [], StringComparer.OrdinalIgnoreCase);
        _enabled = new HashSet<string>(enabled ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Everything on (experimental modules stay off).</summary>
    public static FeatureFlags Default { get; } = new();

    public IReadOnlyCollection<string> DisabledFlags => _disabled;

    public IReadOnlyCollection<string> EnabledFlags => _enabled;

    public bool IsEnabled(string flag, bool enabledByDefault = true)
        => !_disabled.Contains(flag) && (enabledByDefault || _enabled.Contains(flag));

    /// <summary>
    /// Parses <c>"-category:leftovers, +module:health"</c> (also used for the
    /// <c>DEVSWEEP_FEATURES</c> variable): <c>-</c> turns a flag off, <c>+</c> or nothing turns it on.
    /// </summary>
    public FeatureFlags With(string? spec)
    {
        var disabled = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(_enabled, StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (spec ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string flag = raw.TrimStart('-', '+');
            if (raw.StartsWith('-'))
            {
                disabled.Add(flag);
                enabled.Remove(flag);
            }
            else
            {
                enabled.Add(flag);
                disabled.Remove(flag);
            }
        }

        return new FeatureFlags(disabled, enabled);
    }

    /// <summary>
    /// The user's flags: <paramref name="settingsFile"/> (JSON <see cref="FeatureSettings"/>), then
    /// <paramref name="overrides"/> (the <c>DEVSWEEP_FEATURES</c> variable). A missing or unreadable file means defaults.
    /// </summary>
    public static FeatureFlags Load(string? settingsFile, string? overrides)
    {
        FeatureFlags flags = Default;
        if (settingsFile is not null && File.Exists(settingsFile))
        {
            try
            {
                FeatureSettings? settings = JsonSerializer.Deserialize(File.ReadAllText(settingsFile), CoreJsonContext.Default.FeatureSettings);
                if (settings is not null)
                {
                    flags = new FeatureFlags(settings.Disabled, settings.Enabled);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Defaults; a broken settings file must not stop a scan.
            }
        }

        return flags.With(overrides);
    }
}
