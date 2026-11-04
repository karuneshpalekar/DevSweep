namespace DevSweep.Core.Locations;

/// <summary>
/// The named roots rules may use (<c>{LocalAppData}</c>, <c>{npmCache}</c>...).
/// Each is resolved once per registry (one scan) and cached; resolvers may ask
/// for other roots (most tool defaults sit under a Known Folder). Thread-safe.
/// </summary>
public sealed class LocationRegistry
{
    private readonly Dictionary<string, ILocationResolver> _resolvers;
    private readonly Dictionary<string, ResolvedLocation> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public LocationRegistry(ISystemEnvironment environment, IEnumerable<ILocationResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resolvers);
        Environment = environment;
        _resolvers = new Dictionary<string, ILocationResolver>(StringComparer.OrdinalIgnoreCase);
        foreach (ILocationResolver resolver in resolvers)
        {
            _resolvers[resolver.Name] = resolver;
        }
    }

    public ISystemEnvironment Environment { get; }

    /// <summary>Every root name, as registered.</summary>
    public IReadOnlyCollection<string> Names => _resolvers.Values.Select(r => r.Name).ToList();

    /// <summary>The registered spelling of a root name, matched case-insensitively.</summary>
    public string? CanonicalName(string name) => _resolvers.TryGetValue(name, out ILocationResolver? resolver) ? resolver.Name : null;

    /// <summary>Resolves a root (cached). Unknown names and cycles resolve to "not found".</summary>
    public ResolvedLocation Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
        {
            if (_cache.TryGetValue(name, out ResolvedLocation? cached))
            {
                return cached;
            }

            if (!_resolvers.TryGetValue(name, out ILocationResolver? resolver))
            {
                return new ResolvedLocation(name, name, null, LocationSource.NotFound, $"no location called {{{name}}}");
            }

            if (!_resolving.Add(resolver.Name))
            {
                return new ResolvedLocation(resolver.Name, resolver.Description, null, LocationSource.NotFound, "it depends on itself");
            }

            try
            {
                ResolvedLocation resolved = resolver.Resolve(this);
                _cache[name] = resolved;
                return resolved;
            }
            finally
            {
                _resolving.Remove(resolver.Name);
            }
        }
    }

    /// <summary>Every root resolved, in registration order (for <c>devsweep scan --explain</c> and diagnostics).</summary>
    public IReadOnlyList<ResolvedLocation> ResolveAll() => _resolvers.Values.Select(r => Resolve(r.Name)).ToList();

    /// <summary>Turns a template into a path (no wildcards are expanded); null with the reason when its root does not resolve.</summary>
    public string? Expand(PathTemplate template, out ResolvedLocation root)
    {
        ArgumentNullException.ThrowIfNull(template);
        root = Resolve(template.Root);
        return root.Path is null ? null : Path.Join([root.Path, .. template.Segments]);
    }
}
