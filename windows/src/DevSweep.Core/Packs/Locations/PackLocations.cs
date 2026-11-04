using System.Text.RegularExpressions;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Packs.Locations;

/// <summary>The named roots the Windows packs add (each in the tool's own order of precedence).</summary>
public static partial class PackLocations
{
    public static IReadOnlyList<ILocationResolver> All() =>
    [
        new Simple("Windows", "the Windows folder", t => t.Variable("SystemRoot") ?? t.Variable("windir")),
        new Simple("androidAvd", "Android emulators (AVDs)", t => t.Variable("ANDROID_AVD_HOME")
            ?? t.Variable("ANDROID_USER_HOME", "avd")
            ?? t.Variable("ANDROID_SDK_HOME", ".android", "avd")
            ?? t.Default("Profile", ".android", "avd")),
        new Simple("goBuildCache", "Go build cache", t =>
        {
            string? envFile = t.Environment.GetVariable("GOENV") ?? t.Under("RoamingAppData", "go", "env");
            return t.Variable("GOCACHE")
                ?? t.Config(envFile, "GOCACHE", text => ConfigText.KeyValue(text, "GOCACHE"))
                ?? t.Default("LocalAppData", "go-build");
        }),
        new Simple("composerCache", "Composer cache", t => t.Variable("COMPOSER_CACHE_DIR") ?? t.Default("LocalAppData", "Composer")),
        new Simple("chocolateyCache", "Chocolatey download cache", t => t.Config(t.Under("chocolatey", "config", "chocolatey.config"), "cacheLocation", ChocolateyCacheLocation)
            ?? t.Default("Temp", "chocolatey")),
        new Simple("rustupHome", "rustup home", t => t.Variable("RUSTUP_HOME") ?? t.Default("Profile", ".rustup")),
        new Simple("nvmHome", "nvm for Windows", t => t.Variable("NVM_HOME") ?? t.Default("RoamingAppData", "nvm")),
        new Simple("nvmSymlink", "nvm for Windows' active Node link", t => t.Variable("NVM_SYMLINK") ?? t.Default("ProgramFiles", "nodejs")),
        new Simple("fnmDir", "fnm", t => t.Variable("FNM_DIR") ?? t.Default("RoamingAppData", "fnm")),
        new Simple("voltaHome", "Volta", t => t.Variable("VOLTA_HOME") ?? t.Default("LocalAppData", "Volta")),
        new Simple("pyenvRoot", "pyenv-win", t => t.Variable("PYENV_ROOT") ?? t.Variable("PYENV") ?? t.Default("Profile", ".pyenv", "pyenv-win")),
        new Simple("ollamaModels", "Ollama models", t => t.Variable("OLLAMA_MODELS") ?? t.Default("Profile", ".ollama", "models")),
        new Simple("torchHome", "PyTorch cache", t => t.Variable("TORCH_HOME") ?? t.Variable("XDG_CACHE_HOME", "torch") ?? t.Default("Profile", ".cache", "torch")),
    ];

    /// <summary><c>&lt;add key="cacheLocation" value="..." /&gt;</c> in chocolatey.config (empty means the default).</summary>
    internal static string? ChocolateyCacheLocation(string text)
    {
        Match match = CacheLocation().Match(text);
        return match.Success && match.Groups["v"].Value.Trim().Length > 0 ? match.Groups["v"].Value.Trim() : null;
    }

    [GeneratedRegex("<add\\s+key=\"cacheLocation\"\\s+value=\"(?<v>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CacheLocation();

    private sealed class Simple(string name, string description, Func<ResolutionTrail, ResolvedLocation?> resolve) : LocationResolver
    {
        public override string Name => name;

        public override string Description => description;

        protected override ResolvedLocation? Resolve(ResolutionTrail trail) => resolve(trail);
    }
}
