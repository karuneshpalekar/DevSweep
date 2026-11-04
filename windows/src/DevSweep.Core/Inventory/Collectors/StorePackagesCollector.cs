using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Inventory.Collectors;

/// <summary>
/// MSIX and Store packages registered for this user, from the AppModel package
/// repository in the user's registry (the data <c>PackageManager.FindPackagesForUser</c>
/// reads; read directly so the engine needs no WinRT interop and stays AOT safe).
/// Each subkey is a package full name <c>Name_Version_Arch_ResourceId_PublisherId</c>.
/// </summary>
public sealed class StorePackagesCollector : IInventoryCollector
{
    public const string KeyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public InventorySource Source => InventorySource.StorePackages;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.Looked($"HKCU\\{KeyPath}");
        using IRegistryKeyReader? root = run.Context.Registry.Open(RegistryHive.CurrentUser, RegistryView.Registry64, KeyPath);
        var records = new List<AppRecord>();
        if (root is null)
        {
            // Every Windows 11 user has packages (the shell itself is one), so a missing key means we cannot tell.
            run.Problem($"HKCU\\{KeyPath} does not exist, so Store apps could not be listed");
            return records;
        }

        foreach (string fullName in root.SubKeyNames())
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            if (PackageName.TryParse(fullName, out string? name, out string? version, out string? family))
            {
                using IRegistryKeyReader? key = root.OpenSubKey(fullName);
                string? display = key is null ? null : RegistryValues.Text(key, "DisplayName");
                records.Add(new AppRecord
                {
                    Source = Source,
                    Name = display is null || display.StartsWith('@') ? name! : display,
                    Version = version,
                    PackageFamilyName = family,
                    InstallLocation = key is null ? null : RegistryValues.Text(key, "PackageRootFolder"),
                    Origin = fullName,
                });
            }
        }

        return records;
    }
}

/// <summary>Package full names and family names.</summary>
public static class PackageName
{
    /// <summary><c>Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe</c> to name, version and <c>Microsoft.WindowsTerminal_8wekyb3d8bbwe</c>.</summary>
    public static bool TryParse(string fullName, out string? name, out string? version, out string? familyName)
    {
        string[] parts = (fullName ?? string.Empty).Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[4].Length == 0)
        {
            name = version = familyName = null;
            return false;
        }

        name = parts[0];
        version = parts[1];
        familyName = $"{parts[0]}_{parts[4]}";
        return true;
    }
}
