using Microsoft.Win32;

namespace DevSweep.Core.Inventory.Registry;

/// <summary>
/// A registry held in memory, for tests (fake hives): keys by hive, view and
/// path (case-insensitive), with values. Keys under <c>HKEY_CURRENT_USER</c> and
/// <c>HKEY_LOCAL_MACHINE</c> are separate per view, as with WOW64.
/// </summary>
public sealed class InMemoryRegistry : IRegistryReader
{
    private readonly Dictionary<string, Node> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates (or returns) a key and sets values on it.</summary>
    public InMemoryRegistry Set(RegistryHive hive, RegistryView view, string path, params (string Name, object Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);
        Node node = Root(hive, view);
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out Node? child))
            {
                child = new Node(part);
                node.Children[part] = child;
            }

            node = child;
        }

        foreach ((string name, object value) in values)
        {
            node.Values[name] = value;
        }

        return this;
    }

    /// <summary>Makes opening this key throw <see cref="UnauthorizedAccessException"/>, like a key the user may not read.</summary>
    public InMemoryRegistry Deny(RegistryHive hive, RegistryView view, string path)
    {
        _denied.Add(Key(hive, view, path));
        return this;
    }

    public IRegistryKeyReader? Open(RegistryHive hive, RegistryView view, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_denied.Contains(Key(hive, view, path)))
        {
            throw new UnauthorizedAccessException($"Access to {hive}\\{path} is denied.");
        }

        Node node = Root(hive, view);
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out Node? child))
            {
                return null;
            }

            node = child;
        }

        return new Reader(node, $"{hive}\\{path}");
    }

    private Node Root(RegistryHive hive, RegistryView view)
    {
        string key = $"{hive}|{view}";
        if (!_roots.TryGetValue(key, out Node? root))
        {
            root = new Node(hive.ToString());
            _roots[key] = root;
        }

        return root;
    }

    private static string Key(RegistryHive hive, RegistryView view, string path) => $"{hive}|{view}|{path.Trim('\\')}";

    private sealed class Node(string name)
    {
        public string Name { get; } = name;

        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Reader(Node node, string name) : IRegistryKeyReader
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> SubKeyNames() => node.Children.Values.Select(c => c.Name).ToList();

        public object? Value(string valueName) => node.Values.TryGetValue(valueName, out object? value) ? value : null;

        public IRegistryKeyReader? OpenSubKey(string subKey)
            => node.Children.TryGetValue(subKey, out Node? child) ? new Reader(child, $"{Name}\\{subKey}") : null;

        public void Dispose()
        {
        }
    }
}
