namespace DevSweep.Core.Locations.Resolvers;

/// <summary>
/// <c>{npmCache}</c>. npm's own order: <c>npm_config_cache</c>, the user
/// <c>.npmrc</c> (<c>NPM_CONFIG_USERCONFIG</c> or <c>~\.npmrc</c>), the global
/// npmrc (<c>NPM_CONFIG_GLOBALCONFIG</c> or <c>&lt;prefix&gt;\etc\npmrc</c>,
/// prefix <c>NPM_CONFIG_PREFIX</c> or <c>%APPDATA%\npm</c>), then
/// <c>%LOCALAPPDATA%\npm-cache</c>. <c>npm config get cache</c> is never run.
/// </summary>
public sealed class NpmCacheResolver : LocationResolver
{
    public override string Name => "npmCache";

    public override string Description => "npm cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        string? userConfig = trail.Environment.GetVariable("NPM_CONFIG_USERCONFIG") ?? trail.Under("Profile", ".npmrc");
        string? prefix = trail.Environment.GetVariable("NPM_CONFIG_PREFIX") ?? trail.Under("RoamingAppData", "npm");
        string? globalConfig = trail.Environment.GetVariable("NPM_CONFIG_GLOBALCONFIG") ?? (prefix is null ? null : Path.Join(prefix, "etc", "npmrc"));
        return trail.Variable("npm_config_cache")
            ?? trail.Config(userConfig, "cache", text => ConfigText.Ini(text, null, "cache"))
            ?? trail.Config(globalConfig, "cache", text => ConfigText.Ini(text, null, "cache"))
            ?? trail.Default("LocalAppData", "npm-cache");
    }
}

/// <summary>
/// <c>{pnpmStore}</c>: <c>npm_config_store_dir</c>, <c>store-dir</c> in pnpm's
/// global rc (<c>%LOCALAPPDATA%\pnpm\config\rc</c>) or <c>~\.npmrc</c>, then
/// <c>%PNPM_HOME%\store</c>, then <c>%LOCALAPPDATA%\pnpm\store</c>.
/// </summary>
public sealed class PnpmStoreResolver : LocationResolver
{
    public override string Name => "pnpmStore";

    public override string Description => "pnpm store";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("npm_config_store_dir")
            ?? trail.Config(trail.Under("LocalAppData", "pnpm", "config", "rc"), "store-dir", text => ConfigText.Ini(text, null, "store-dir"))
            ?? trail.Config(trail.Under("Profile", ".npmrc"), "store-dir", text => ConfigText.Ini(text, null, "store-dir"))
            ?? trail.Variable("PNPM_HOME", "store")
            ?? trail.Default("LocalAppData", "pnpm", "store");
    }
}

/// <summary><c>{yarnCache}</c> (Yarn classic): <c>YARN_CACHE_FOLDER</c>, <c>cache-folder</c> in <c>~\.yarnrc</c>, then <c>%LOCALAPPDATA%\Yarn\Cache</c>.</summary>
public sealed class YarnCacheResolver : LocationResolver
{
    public override string Name => "yarnCache";

    public override string Description => "Yarn cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("YARN_CACHE_FOLDER")
            ?? trail.Config(trail.Under("Profile", ".yarnrc"), "cache-folder", text => ConfigText.Yarnrc(text, "cache-folder"))
            ?? trail.Default("LocalAppData", "Yarn", "Cache");
    }
}

/// <summary>
/// <c>{pipCache}</c>. pip's order: <c>PIP_CACHE_DIR</c>, then <c>cache-dir</c> in
/// <c>[global]</c> of <c>PIP_CONFIG_FILE</c>, the user <c>%APPDATA%\pip\pip.ini</c>,
/// the legacy <c>~\pip\pip.ini</c>, the global <c>%ProgramData%\pip\pip.ini</c>;
/// then <c>%LOCALAPPDATA%\pip\Cache</c>.
/// </summary>
public sealed class PipCacheResolver : LocationResolver
{
    public override string Name => "pipCache";

    public override string Description => "pip cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        static string? Read(string text) => ConfigText.Ini(text, "global", "cache-dir", "cache_dir");
        return trail.Variable("PIP_CACHE_DIR")
            ?? trail.Config(trail.Environment.GetVariable("PIP_CONFIG_FILE"), "cache-dir", Read)
            ?? trail.Config(trail.Under("RoamingAppData", "pip", "pip.ini"), "cache-dir", Read)
            ?? trail.Config(trail.Under("Profile", "pip", "pip.ini"), "cache-dir", Read)
            ?? trail.Config(trail.Under("ProgramData", "pip", "pip.ini"), "cache-dir", Read)
            ?? trail.Default("LocalAppData", "pip", "Cache");
    }
}

/// <summary><c>{uvCache}</c>: <c>UV_CACHE_DIR</c>, <c>cache-dir</c> in <c>%APPDATA%\uv\uv.toml</c>, then <c>%LOCALAPPDATA%\uv\cache</c>.</summary>
public sealed class UvCacheResolver : LocationResolver
{
    public override string Name => "uvCache";

    public override string Description => "uv cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("UV_CACHE_DIR")
            ?? trail.Config(trail.Under("RoamingAppData", "uv", "uv.toml"), "cache-dir", text => ConfigText.TomlTopLevel(text, "cache-dir"))
            ?? trail.Default("LocalAppData", "uv", "cache");
    }
}

/// <summary>
/// <c>{nugetPackages}</c>: <c>NUGET_PACKAGES</c>, <c>globalPackagesFolder</c> in the
/// user <c>%APPDATA%\NuGet\NuGet.Config</c> (relative values against the file's folder),
/// then <c>~\.nuget\packages</c>.
/// </summary>
public sealed class NuGetPackagesResolver : LocationResolver
{
    public override string Name => "nugetPackages";

    public override string Description => "NuGet global packages folder";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("NUGET_PACKAGES")
            ?? trail.Config(trail.Under("RoamingAppData", "NuGet", "NuGet.Config"), "globalPackagesFolder",
                text => ConfigText.NuGetConfig(text, "globalPackagesFolder"), relativeToFile: true)
            ?? trail.Default("Profile", ".nuget", "packages");
    }
}

/// <summary><c>{nugetHttpCache}</c>: <c>NUGET_HTTP_CACHE_PATH</c>, then <c>%LOCALAPPDATA%\NuGet\v3-cache</c> (NuGet has no config setting for it).</summary>
public sealed class NuGetHttpCacheResolver : LocationResolver
{
    public override string Name => "nugetHttpCache";

    public override string Description => "NuGet HTTP cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("NUGET_HTTP_CACHE_PATH") ?? trail.Default("LocalAppData", "NuGet", "v3-cache");
    }
}
