using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>PowerShell commands for a fix, and whether they need an administrator.</summary>
public sealed record FixCommand(IReadOnlyList<string> Commands, bool NeedsAdmin);

/// <summary>
/// Upgrade, install and removal commands per owning tool (winget, Scoop, Chocolatey, nvm-windows,
/// fnm, Volta, pyenv-win, uv, conda, the Python install manager, rustup, mise, asdf, MSI). Values
/// are always single-quoted literals. Machine-wide changes are marked as needing an administrator;
/// nothing is ever elevated silently.
/// </summary>
public static class FixCommands
{
    /// <summary>winget ids of the usual installers, for installing a release line.</summary>
    public static string? WingetId(string tool, string cycle, Installation? like = null) => tool switch
    {
        "node" => "OpenJS.NodeJS.LTS",
        "python" => $"Python.Python.{cycle}",
        "java" => like?.Product == "microsoft-build-of-openjdk" ? $"Microsoft.OpenJDK.{cycle}" : $"EclipseAdoptium.Temurin.{cycle}.JDK",
        "go" => "GoLang.Go",
        "ruby" => $"RubyInstallerTeam.Ruby.{cycle}",
        "php" => $"PHP.PHP.{cycle}",
        "rust" => "Rustlang.Rustup",
        "dotnet" => $"Microsoft.DotNet.SDK.{VersionText.Major(cycle)}",
        "postgres" => $"PostgreSQL.PostgreSQL.{cycle}",
        "deno" => "DenoLand.Deno",
        "bun" => "Oven-sh.Bun",
        _ => null,
    };

    /// <summary>Installs <paramref name="cycle"/> with the tool that owns the user's current install (falling back to winget).</summary>
    public static FixCommand? Install(string tool, string cycle, InstallSource via, Installation? like = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(cycle);
        string q = PowerShellText.Quote(cycle);
        return (tool, via) switch
        {
            ("node", InstallSource.Nvm) => new([$"nvm install {q}", $"nvm use {q}"], false),
            ("node", InstallSource.Fnm) => new([$"fnm install {q}", $"fnm default {q}"], false),
            ("node", InstallSource.Volta) => new([$"volta install {PowerShellText.Quote("node@" + cycle)}"], false),
            ("node", InstallSource.Scoop) => new(["scoop install nodejs-lts"], false),
            ("python", InstallSource.Pyenv) => new([$"pyenv install {q}", $"pyenv global {q}"], false),
            ("python", InstallSource.Uv) => new([$"uv python install {q}"], false),
            ("python", InstallSource.PythonInstallManager) => new([$"py install {q}"], false),
            ("python", InstallSource.Scoop) => new([$"scoop install {PowerShellText.Quote("versions/python" + cycle.Replace(".", string.Empty, StringComparison.Ordinal))}"], false),
            ("rust", _) => new(["rustup update stable", "rustup default stable"], false),
            ("deno", InstallSource.DenoInstaller) => new(["deno upgrade"], false),
            ("bun", InstallSource.BunInstaller) => new(["bun upgrade"], false),
            (_, InstallSource.Mise) => new([$"mise use -g {PowerShellText.Quote(tool + "@" + cycle)}"], false),
            (_, InstallSource.Asdf) => new([$"asdf install {AsdfName(tool)} {PowerShellText.Quote("latest:" + cycle)}"], false),
            (_, InstallSource.Chocolatey) when ChocolateyId(tool, cycle) is { } id => new([$"choco install {PowerShellText.Quote(id)} -y"], true),
            _ when WingetId(tool, cycle, like) is { } id => new([$"winget install --id {PowerShellText.Quote(id)} --exact"], false),
            ("flutter", _) => new(["flutter upgrade"], false),
            _ => null,
        };
    }

    /// <summary>Installs the newest patch of the same line with the owning tool; null when that tool has no such command.</summary>
    public static FixCommand? Upgrade(Installation install, string tool)
    {
        ArgumentNullException.ThrowIfNull(install);
        return install.Source switch
        {
            InstallSource.Scoop when install.SourceDetail is { } app => new([$"scoop update {PowerShellText.Quote(app)}"], false),
            InstallSource.Chocolatey when install.SourceDetail is { } id => new([$"choco upgrade {PowerShellText.Quote(id)} -y"], true),
            InstallSource.Winget when (install.WingetId ?? install.SourceDetail) is { } id => new([$"winget upgrade --id {PowerShellText.Quote(id)} --exact"], false),
            _ when install.WingetId is { } id => new([$"winget upgrade --id {PowerShellText.Quote(id)} --exact"], false),
            InstallSource.Nvm or InstallSource.Fnm or InstallSource.Volta or InstallSource.Pyenv or InstallSource.Uv or InstallSource.Mise or InstallSource.Asdf
                or InstallSource.PythonInstallManager => Install(tool, install.Cycle, install.Source, install),
            InstallSource.Rustup => new(["rustup update"], false),
            _ => null,
        };
    }

    /// <summary>Removes one install with the tool that owns it; null when DevSweep should not offer it (IDE, CI, unknown owner).</summary>
    public static FixCommand? Remove(Installation install, string tool, string? profile)
    {
        ArgumentNullException.ThrowIfNull(install);
        bool userOwned = profile is not null && ProbeKit.IsInside(install.Path, profile);
        string v = PowerShellText.Quote(install.Version);
        return install.Source switch
        {
            InstallSource.Nvm => new([$"nvm uninstall {v}"], false),
            InstallSource.Fnm => new([$"fnm uninstall {v}"], false),
            InstallSource.Pyenv => new([$"pyenv uninstall {v}"], false),
            InstallSource.Uv => new([$"uv python uninstall {v}"], false),
            InstallSource.PythonInstallManager => new([$"py uninstall {PowerShellText.Quote(install.Cycle)}"], false),
            InstallSource.Mise => new([$"mise uninstall {PowerShellText.Quote(tool + "@" + install.Version)}"], false),
            InstallSource.Asdf => new([$"asdf uninstall {AsdfName(tool)} {v}"], false),
            InstallSource.Rustup when install.SourceDetail is { } name => new([$"rustup toolchain uninstall {PowerShellText.Quote(name)}"], false),
            InstallSource.Scoop when install.SourceDetail is { } app => new([$"scoop cleanup {PowerShellText.Quote(app)}"], false),
            InstallSource.Chocolatey when install.SourceDetail is { } id => new([$"choco uninstall {PowerShellText.Quote(id)} -y"], true),
            InstallSource.Winget when (install.WingetId ?? install.SourceDetail) is { } id => new([$"winget uninstall --id {PowerShellText.Quote(id)} --exact"], false),
            InstallSource.StorePython when install.SourceDetail is { } family
                => new([$"Get-AppxPackage -Name {PowerShellText.Quote(family.Split('_')[0])} | Remove-AppxPackage"], false),
            InstallSource.Conda when install.SourceDetail?.StartsWith("env ", StringComparison.Ordinal) == true
                => new([$"conda env remove --prefix {PowerShellText.Quote(install.Path)} --yes"], false),
            InstallSource.Volta or InstallSource.IdeDownloaded or InstallSource.SdkFolder or InstallSource.DenoInstaller or InstallSource.BunInstaller or InstallSource.Fvm
                => new([PowerShellText.RecycleFolder(install.Path)], !userOwned),
            InstallSource.RubyInstaller when HealthFiles.IsFile(System.IO.Path.Join(install.Path, "unins000.exe"))
                => new([PowerShellText.Run(System.IO.Path.Join(install.Path, "unins000.exe"))], !userOwned),
            InstallSource.PostgresInstaller when HealthFiles.IsFile(System.IO.Path.Join(install.Path, "uninstall-postgresql.exe"))
                => new([PowerShellText.Run(System.IO.Path.Join(install.Path, "uninstall-postgresql.exe"))], true),
            _ when install.WingetId is { } id => new([$"winget uninstall --id {PowerShellText.Quote(id)} --exact"], false),
            _ when install.ProductCode is { } code => new([$"Start-Process msiexec.exe -ArgumentList '/x', {PowerShellText.Quote("{" + code + "}")} -Wait"], !userOwned),
            InstallSource.PythonOrg or InstallSource.NodejsOrg or InstallSource.GoOrg or InstallSource.JdkInstaller or InstallSource.DotnetInstaller or InstallSource.RubyInstaller
                or InstallSource.PostgresInstaller => new(["Start-Process 'ms-settings:appsfeatures'"], false),
            _ => null,
        };
    }

    private static string AsdfName(string tool) => tool switch
    {
        "node" => "nodejs",
        "go" => "golang",
        _ => tool,
    };

    private static string? ChocolateyId(string tool, string cycle) => tool switch
    {
        "node" => "nodejs-lts",
        "python" => "python" + cycle.Replace(".", string.Empty, StringComparison.Ordinal),
        "java" => $"temurin{cycle}",
        "go" => "golang",
        "ruby" => "ruby",
        "php" => "php",
        "postgres" => $"postgresql{cycle}",
        "dotnet" => $"dotnet-{cycle}-sdk",
        _ => null,
    };
}
