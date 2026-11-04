namespace DevSweep.Core.Locations;

/// <summary>Resolves one named location (a Known Folder or a tool's folder).</summary>
public interface ILocationResolver
{
    /// <summary>The root name rules use, without braces: <c>npmCache</c> for <c>{npmCache}</c>.</summary>
    string Name { get; }

    /// <summary>What it is, in words ("npm cache").</summary>
    string Description { get; }

    ResolvedLocation Resolve(LocationRegistry locations);
}

/// <summary>
/// Base for resolvers that try sources in the tool's own order of precedence and
/// stop at the first answer: <c>trail.Environment("X") ?? trail.Config(...) ?? trail.Default(...)</c>.
/// Every source tried is remembered, so the answer says what was checked.
/// </summary>
public abstract class LocationResolver : ILocationResolver
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public ResolvedLocation Resolve(LocationRegistry locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var trail = new ResolutionTrail(Name, Description, locations);
        return Resolve(trail) ?? trail.NotFound();
    }

    /// <summary>The first source that answers, in the tool's order; null when none does.</summary>
    protected abstract ResolvedLocation? Resolve(ResolutionTrail trail);
}

/// <summary>Tries the sources of one location and remembers what was looked at.</summary>
public sealed class ResolutionTrail(string name, string description, LocationRegistry locations)
{
    private readonly List<string> _looked = [];

    public ISystemEnvironment Environment => locations.Environment;

    /// <summary>Another location's folder (a Known Folder, or another tool's), or null.</summary>
    public string? Root(string rootName) => locations.Resolve(rootName).Path;

    /// <summary>Another location's folder joined with <paramref name="parts"/>, or null.</summary>
    public string? Under(string rootName, params string[] parts)
        => Root(rootName) is { } root ? Path.Join([root, .. parts]) : null;

    /// <summary>The value of an environment variable (plus <paramref name="parts"/>), when set.</summary>
    public ResolvedLocation? Variable(string variable, params string[] parts)
    {
        string? value = Environment.GetVariable(variable);
        if (value is null || ConfigText.Expand(value.Trim().Trim('"'), this) is not { Length: > 0 } expanded || !Path.IsPathFullyQualified(expanded))
        {
            _looked.Add(value is null ? $"{variable} not set" : $"{variable} is not a full path");
            return null;
        }

        return Found(Path.Join([expanded, .. parts]), LocationSource.Environment, variable);
    }

    /// <summary>
    /// A setting in the tool's config file. <paramref name="read"/> gets the file's
    /// text and returns the raw value or null; it is expanded (<c>%VAR%</c>,
    /// <c>${VAR}</c>, <c>~</c>) and, when relative, taken against the file's folder
    /// only if <paramref name="relativeToFile"/> (NuGet's rule); otherwise ignored.
    /// </summary>
    public ResolvedLocation? Config(string? file, string setting, Func<string, string?> read, bool relativeToFile = false, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (file is null)
        {
            return null;
        }

        string? text = Environment.ReadAllText(file);
        if (text is null)
        {
            _looked.Add($"no {file}");
            return null;
        }

        string? raw = read(text);
        if (raw is null || ConfigText.Expand(raw, this) is not { Length: > 0 } value)
        {
            _looked.Add($"no {setting} in {file}");
            return null;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            if (!relativeToFile || Path.GetDirectoryName(file) is not { } folder)
            {
                _looked.Add($"{setting} in {file} is not a full path");
                return null;
            }

            value = Path.GetFullPath(Path.Join(folder, value));
        }

        return Found(Path.Join([value, .. parts]), LocationSource.ConfigFile, $"{setting} in {file}");
    }

    /// <summary>The tool's default under another root; the detail lists what was looked at first.</summary>
    public ResolvedLocation? Default(string rootName, params string[] parts)
    {
        string? path = Under(rootName, parts);
        if (path is null)
        {
            _looked.Add($"{{{rootName}}} could not be resolved");
            return null;
        }

        string detail = _looked.Count == 0 ? "the default" : $"the default; {string.Join(", ", _looked)}";
        return new ResolvedLocation(name, description, path, LocationSource.Default, detail);
    }

    /// <summary>An answer from somewhere else (a Known Folder, a command).</summary>
    public ResolvedLocation Found(string path, LocationSource source, string detail)
        => new(name, description, Path.TrimEndingDirectorySeparator(path), source, detail);

    /// <summary>Records something looked at that gave no answer.</summary>
    public void Looked(string what) => _looked.Add(what);

    public ResolvedLocation NotFound()
        => new(name, description, null, LocationSource.NotFound, _looked.Count == 0 ? "nothing to look at" : string.Join(", ", _looked));
}
