using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Inventory.Collectors;

/// <summary>
/// The Uninstall key (what Settings, Apps lists) in one hive and view. Every
/// entry with a DisplayName counts, system components and updates included:
/// for leftover detection a wider "installed" is the safe side.
/// </summary>
public sealed class UninstallCollector(InventorySource source, RegistryHive hive, RegistryView view) : IInventoryCollector
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public InventorySource Source { get; } = source;

    /// <summary>The four views PLAN 4.3 asks for.</summary>
    public static IReadOnlyList<UninstallCollector> All() =>
    [
        new(InventorySource.UninstallMachine64, RegistryHive.LocalMachine, RegistryView.Registry64),
        new(InventorySource.UninstallMachine32, RegistryHive.LocalMachine, RegistryView.Registry32),
        new(InventorySource.UninstallUser64, RegistryHive.CurrentUser, RegistryView.Registry64),
        new(InventorySource.UninstallUser32, RegistryHive.CurrentUser, RegistryView.Registry32),
    ];

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        string where = $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{KeyPath} ({(view == RegistryView.Registry64 ? "64" : "32")}-bit)";
        run.Looked(where);
        using IRegistryKeyReader? root = run.Context.Registry.Open(hive, view, KeyPath);
        var records = new List<AppRecord>();
        if (root is null)
        {
            // HKCU often has no Uninstall key at all (nothing installed per user); HKLM always has one.
            if (hive == RegistryHive.LocalMachine)
            {
                run.Problem($"{where} does not exist");
            }

            return records;
        }

        foreach (string name in root.SubKeyNames())
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                using IRegistryKeyReader? key = root.OpenSubKey(name);
                if (key is not null && Read(key, name) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                run.Problem($"{where}\\{name} could not be read: {ex.Message}");
            }
        }

        return records;
    }

    private AppRecord? Read(IRegistryKeyReader key, string name)
    {
        if (RegistryValues.Text(key, "DisplayName") is not { } displayName)
        {
            return null;
        }

        var exes = new List<string>();
        if (ExePath.FromCommand(RegistryValues.Text(key, "DisplayIcon")) is { } icon)
        {
            exes.Add(icon);
        }

        bool msi = RegistryValues.Number(key, "WindowsInstaller") == 1;
        return new AppRecord
        {
            Source = Source,
            Name = displayName,
            Publisher = RegistryValues.Text(key, "Publisher"),
            Version = RegistryValues.Text(key, "DisplayVersion"),
            ProductCode = msi || ProductCodes.IsGuid(name) ? ProductCodes.Normalize(name) : null,
            PackageId = RegistryValues.Text(key, "WinGetPackageIdentifier"),
            InstallLocation = RegistryValues.Text(key, "InstallLocation")?.Trim().Trim('"').TrimEnd('\\') is { Length: > 0 } location ? location : null,
            ExePaths = exes,
            Origin = key.Name,
        };
    }
}

/// <summary>Reading typed registry values.</summary>
public static class RegistryValues
{
    public static string? Text(IRegistryKeyReader key, string name)
        => key?.Value(name) is string { Length: > 0 } text && text.Trim().Length > 0 ? text.Trim() : null;

    public static long? Number(IRegistryKeyReader key, string name) => key?.Value(name) switch
    {
        int i => i,
        long l => l,
        _ => null,
    };
}

/// <summary>MSI product codes: <c>{GUID}</c>, compared upper case without braces.</summary>
public static class ProductCodes
{
    public static bool IsGuid(string? text) => text is not null && Guid.TryParse(text.Trim('{', '}'), out _);

    public static string? Normalize(string? text)
        => text is not null && Guid.TryParse(text.Trim().Trim('{', '}'), out Guid guid) ? guid.ToString("D").ToUpperInvariant() : null;
}

/// <summary>Program paths out of registry command lines and icon references.</summary>
public static class ExePath
{
    /// <summary>
    /// <c>"C:\App\app.exe",0</c>, <c>C:\App\app.exe /uninstall</c> or <c>%ProgramFiles%\App\app.exe</c>
    /// to <c>C:\App\app.exe</c>; null when there is no <c>.exe</c> path.
    /// </summary>
    public static string? FromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"') && text.IndexOf('"', 1) is int close and > 1)
        {
            text = text[1..close];
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0)
        {
            return null;
        }

        string path = text[..(exe + 4)].Trim();
        return Path.IsPathFullyQualified(path) ? path : null;
    }
}
