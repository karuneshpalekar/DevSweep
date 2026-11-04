using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;

namespace DevSweep.Core.Inventory.Collectors;

/// <summary>
/// App Paths registrations (<c>code.exe</c> to its path) in HKLM (both views) and
/// HKCU. An entry whose program no longer exists is stale and does not count.
/// </summary>
public sealed class AppPathsCollector(Func<string, bool>? fileExists = null) : IInventoryCollector
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    public InventorySource Source => InventorySource.AppPaths;

    public IReadOnlyList<AppRecord> Collect(CollectorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var records = new List<AppRecord>();
        foreach ((RegistryHive hive, RegistryView view) in (ReadOnlySpan<(RegistryHive, RegistryView)>)
            [(RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64)])
        {
            string where = $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{KeyPath} ({(view == RegistryView.Registry64 ? "64" : "32")}-bit)";
            run.Looked(where);
            using IRegistryKeyReader? root = run.Context.Registry.Open(hive, view, KeyPath);
            foreach (string exe in root?.SubKeyNames() ?? [])
            {
                try
                {
                    using IRegistryKeyReader? key = root!.OpenSubKey(exe);
                    string? path = key is null ? null : ExePath.FromCommand(RegistryValues.Text(key, string.Empty));
                    if (path is not null && _fileExists(path))
                    {
                        records.Add(new AppRecord
                        {
                            Source = Source,
                            Name = Path.GetFileNameWithoutExtension(exe),
                            ExePaths = [path],
                            InstallLocation = Path.GetDirectoryName(path),
                            Origin = key!.Name,
                        });
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    run.Problem($"{where}\\{exe} could not be read: {ex.Message}");
                }
            }
        }

        return records;
    }
}
