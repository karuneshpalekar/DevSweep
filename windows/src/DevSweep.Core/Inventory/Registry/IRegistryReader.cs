using Microsoft.Win32;

namespace DevSweep.Core.Inventory.Registry;

/// <summary>Read-only registry access, so inventory sources can be tested against fake hives.</summary>
public interface IRegistryReader
{
    /// <summary>Opens <paramref name="path"/> under a hive in a view (64- or 32-bit); null when it does not exist.</summary>
    /// <exception cref="UnauthorizedAccessException">The key exists but may not be read.</exception>
    IRegistryKeyReader? Open(RegistryHive hive, RegistryView view, string path);
}

/// <summary>An open registry key.</summary>
public interface IRegistryKeyReader : IDisposable
{
    /// <summary>The key's full name, for messages.</summary>
    string Name { get; }

    IReadOnlyList<string> SubKeyNames();

    /// <summary>A value (string, int, string[]...); null when missing. <c>""</c> is the default value.</summary>
    object? Value(string name);

    IRegistryKeyReader? OpenSubKey(string name);

    /// <summary>The names of the key's values (added for M3a; empty for readers that do not list them).</summary>
    IReadOnlyList<string> ValueNames() => [];
}

/// <summary>The real registry (<see cref="RegistryKey.OpenBaseKey"/>), read only.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public static WindowsRegistryReader Instance { get; } = new();

    public IRegistryKeyReader? Open(RegistryHive hive, RegistryView view, string path)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
        RegistryKey? key = root.OpenSubKey(path, writable: false);
        return key is null ? null : new Key(key);
    }

    private sealed class Key(RegistryKey key) : IRegistryKeyReader
    {
        public string Name => key.Name;

        public IReadOnlyList<string> SubKeyNames() => key.GetSubKeyNames();

        public object? Value(string name) => key.GetValue(name, null, RegistryValueOptions.None);

        public IReadOnlyList<string> ValueNames() => key.GetValueNames();

        public IRegistryKeyReader? OpenSubKey(string name)
        {
            RegistryKey? child = key.OpenSubKey(name, writable: false);
            return child is null ? null : new Key(child);
        }

        public void Dispose() => key.Dispose();
    }
}
