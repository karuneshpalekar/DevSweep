namespace DevSweep.Core.Locations.Resolvers;

/// <summary><c>{scoop}</c>: <c>SCOOP</c>, <c>root_path</c> in Scoop's <c>~\.config\scoop\config.json</c>, then <c>~\scoop</c>.</summary>
public sealed class ScoopRootResolver : LocationResolver
{
    public override string Name => "scoop";

    public override string Description => "Scoop (per user)";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("SCOOP")
            ?? trail.Config(ScoopConfig(trail), "root_path", text => ConfigText.JsonString(text, "root_path"))
            ?? trail.Default("Profile", "scoop");
    }

    internal static string? ScoopConfig(ResolutionTrail trail)
        => trail.Environment.GetVariable("XDG_CONFIG_HOME") is { } xdg
            ? Path.Join(xdg, "scoop", "config.json")
            : trail.Under("Profile", ".config", "scoop", "config.json");
}

/// <summary><c>{scoopGlobal}</c>: <c>SCOOP_GLOBAL</c>, <c>global_path</c> in Scoop's config, then <c>%ProgramData%\scoop</c>.</summary>
public sealed class ScoopGlobalResolver : LocationResolver
{
    public override string Name => "scoopGlobal";

    public override string Description => "Scoop (all users)";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("SCOOP_GLOBAL")
            ?? trail.Config(ScoopRootResolver.ScoopConfig(trail), "global_path", text => ConfigText.JsonString(text, "global_path"))
            ?? trail.Default("ProgramData", "scoop");
    }
}

/// <summary><c>{chocolatey}</c>: <c>ChocolateyInstall</c>, then <c>%ProgramData%\chocolatey</c>.</summary>
public sealed class ChocolateyResolver : LocationResolver
{
    public override string Name => "chocolatey";

    public override string Description => "Chocolatey";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("ChocolateyInstall") ?? trail.Default("ProgramData", "chocolatey");
    }
}

/// <summary>
/// <c>{wingetPackages}</c>: where winget puts portable packages for this user:
/// <c>installBehavior.portablePackageUserRoot</c> in winget's settings.json, then
/// <c>%LOCALAPPDATA%\Microsoft\WinGet\Packages</c>.
/// </summary>
public sealed class WingetPackagesResolver : LocationResolver
{
    public override string Name => "wingetPackages";

    public override string Description => "winget portable packages (per user)";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Config(SettingsFile(trail), "portablePackageUserRoot",
                text => ConfigText.JsonString(text, "installBehavior.portablePackageUserRoot"))
            ?? trail.Default("LocalAppData", "Microsoft", "WinGet", "Packages");
    }

    internal static string? SettingsFile(ResolutionTrail trail)
        => trail.Under("LocalAppData", "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "settings.json");
}

/// <summary><c>{wingetMachinePackages}</c>: <c>portablePackageMachineRoot</c> in winget's settings.json, then <c>%ProgramFiles%\WinGet\Packages</c>.</summary>
public sealed class WingetMachinePackagesResolver : LocationResolver
{
    public override string Name => "wingetMachinePackages";

    public override string Description => "winget portable packages (all users)";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Config(WingetPackagesResolver.SettingsFile(trail), "portablePackageMachineRoot",
                text => ConfigText.JsonString(text, "installBehavior.portablePackageMachineRoot"))
            ?? trail.Default("ProgramFiles", "WinGet", "Packages");
    }
}
