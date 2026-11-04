using System.Text.Json;
using System.Text.RegularExpressions;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Common;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// Works out which program really runs behind a PATH match: Store aliases,
/// Scoop shims (<c>name.shim</c>), links on the way (nvm-windows' <c>NVM_SYMLINK</c>,
/// Scoop's <c>current</c>, fnm aliases), Volta's and pyenv-win's shims. Only link
/// targets and small text files are read; nothing is run.
/// </summary>
public static partial class ShimResolver
{
    public static (string Runs, string? Via) Resolve(PathHit hit, ISystemEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(environment);
        if (hit.Alias is { } alias)
        {
            return (alias.Target, "Microsoft Store alias");
        }

        string path = hit.Path;
        string? via = null;
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);

        string shim = Path.Join(directory, stem + ".shim");
        if (HealthFiles.ReadText(shim, 16 * 1024) is { } shimText && ShimTarget().Match(shimText) is { Success: true } m)
        {
            path = m.Groups["p"].Value.Trim();
            via = "Scoop shim";
        }
        else if (VoltaTarget(directory, stem, environment) is { } volta)
        {
            path = volta;
            via = "Volta (default from platform.json)";
        }
        else if (PyenvTarget(directory, stem) is { } pyenv)
        {
            path = pyenv;
            via = "pyenv-win (global version file)";
        }

        string final = FollowLinks(path);
        if (!string.Equals(final, path, StringComparison.OrdinalIgnoreCase))
        {
            via = via is null ? "link" : via + ", link";
        }

        return (final, via);
    }

    /// <summary>Replaces every link (junction or symbolic link) on the way with its target, by reading the link only.</summary>
    public static string FollowLinks(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string current = path;
        for (int guard = 0; guard < 16; guard++)
        {
            string? changed = null;
            string? probe = current;
            while (probe is not null)
            {
                if (FileProbe.Stat(probe) is { IsReparsePoint: true, IsCloudPlaceholder: false } entry)
                {
                    string? target = LinkTarget(probe, entry.IsDirectory);
                    if (target is not null)
                    {
                        string rest = Path.GetRelativePath(probe, current);
                        changed = rest == "." ? target : Path.GetFullPath(Path.Join(target, rest));
                        break;
                    }
                }

                probe = Path.GetDirectoryName(probe);
            }

            if (changed is null || string.Equals(changed, current, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = changed;
        }

        return current;
    }

    private static string? LinkTarget(string path, bool isDirectory)
    {
        try
        {
            string? target = isDirectory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;
            if (target is null)
            {
                return null;
            }

            target = target.StartsWith(@"\??\", StringComparison.Ordinal) ? target[4..] : target;
            return Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(Path.Join(Path.GetDirectoryName(path), target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? VoltaTarget(string directory, string stem, ISystemEnvironment environment)
    {
        string? home = environment.GetVariable("VOLTA_HOME") ?? (environment.KnownFolder("LocalAppData") is { } local ? Path.Join(local, "Volta") : null);
        if (home is null || !string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.Join(home, "bin"), StringComparison.OrdinalIgnoreCase)
            || !stem.Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? text = HealthFiles.ReadText(Path.Join(home, "tools", "user", "platform.json"), 64 * 1024);
        if (text is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("node", out JsonElement node) && node.TryGetProperty("runtime", out JsonElement runtime)
                && runtime.GetString() is { } version
                ? Path.Join(home, "tools", "image", "node", version, "node.exe")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PyenvTarget(string directory, string stem)
    {
        if (!directory.EndsWith(Path.Join("pyenv-win", "shims"), StringComparison.OrdinalIgnoreCase)
            || !stem.StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory))!;
        string? version = HealthFiles.ReadText(Path.Join(root, "version"), 4096) is { } text ? HealthFiles.FirstLine(text).Split(' ')[0] : null;
        return string.IsNullOrEmpty(version) ? null : Path.Join(root, "versions", version, "python.exe");
    }

    [GeneratedRegex(@"^\s*path\s*=\s*""?(?<p>[^""\r\n]+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ShimTarget();
}
