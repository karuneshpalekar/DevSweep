using DevSweep.Core.Locations;
using Xunit;

namespace DevSweep.Core.Tests.Locations;

/// <summary>Every tool location: config first where the tool reads config first, then env, then the default; each says what it used.</summary>
public sealed class ResolverTests
{
    private static readonly string Root = Path.Join(Path.GetTempPath(), "devsweep-resolvers");
    private readonly FixedEnvironment _env = FixedEnvironment.UnderRoot(Root, readRealFiles: false);

    private string Profile => _env.Folders["Profile"];

    private string Local => _env.Folders["LocalAppData"];

    private string Roaming => _env.Folders["RoamingAppData"];

    private static string Json(string text) => "\"" + text.Replace(@"\", @"\\", StringComparison.Ordinal) + "\"";

    private static string Elsewhere(params string[] parts) => Path.Join([Root, "elsewhere", .. parts]);

    private ResolvedLocation Resolve(string name) => new LocationRegistry(_env, BuiltInLocations.All()).Resolve(name);

    [Fact]
    public void Npm_cache_default_lists_what_was_looked_at()
    {
        ResolvedLocation npm = Resolve("npmCache");

        Assert.Equal(Path.Join(Local, "npm-cache"), npm.Path);
        Assert.Equal(LocationSource.Default, npm.Source);
        Assert.Contains("npm_config_cache not set", npm.Detail, StringComparison.Ordinal);
        Assert.Contains(Path.Join(Profile, ".npmrc"), npm.Detail, StringComparison.Ordinal);
        Assert.Contains("npm cache: ", npm.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Npm_cache_from_user_npmrc_beats_global_npmrc_and_expands_variables()
    {
        _env.Variables["DEVDRIVE"] = Elsewhere();
        _env.Files[Path.Join(Profile, ".npmrc")] = "; comment\nregistry=https://example\ncache = \"${DEVDRIVE}" + Path.DirectorySeparatorChar + "npm\"\n";
        _env.Files[Path.Join(Roaming, "npm", "etc", "npmrc")] = $"cache={Elsewhere("global")}\n";

        ResolvedLocation npm = Resolve("npmCache");

        Assert.Equal(Elsewhere("npm"), npm.Path);
        Assert.Equal(LocationSource.ConfigFile, npm.Source);
        Assert.Equal($"cache in {Path.Join(Profile, ".npmrc")}", npm.Detail);
    }

    [Fact]
    public void Npm_cache_variable_beats_config_and_global_npmrc_is_used_when_no_user_one()
    {
        _env.Files[Path.Join(Roaming, "npm", "etc", "npmrc")] = $"cache={Elsewhere("global")}\n";
        Assert.Equal(Elsewhere("global"), Resolve("npmCache").Path);

        _env.Variables["NPM_CONFIG_CACHE"] = Elsewhere("env");
        ResolvedLocation fromEnv = Resolve("npmCache");
        Assert.Equal(Elsewhere("env"), fromEnv.Path);
        Assert.Equal(LocationSource.Environment, fromEnv.Source);
    }

    [Fact]
    public void Npm_userconfig_variable_moves_the_npmrc()
    {
        string custom = Elsewhere("custom.npmrc");
        _env.Variables["NPM_CONFIG_USERCONFIG"] = custom;
        _env.Files[custom] = $"cache={Elsewhere("c")}";

        Assert.Equal(Elsewhere("c"), Resolve("npmCache").Path);
    }

    [Fact]
    public void Pip_cache_reads_the_global_section_of_pip_ini_in_pip_order()
    {
        Assert.Equal(Path.Join(Local, "pip", "Cache"), Resolve("pipCache").Path);

        _env.Files[Path.Join(_env.Folders["ProgramData"], "pip", "pip.ini")] = $"[global]\ncache-dir = {Elsewhere("machine")}\n";
        Assert.Equal(Elsewhere("machine"), Resolve("pipCache").Path);

        _env.Files[Path.Join(Roaming, "pip", "pip.ini")] = $"[install]\ncache-dir = {Elsewhere("wrong")}\n[global]\ncache_dir={Elsewhere("user")}\n";
        ResolvedLocation user = Resolve("pipCache");
        Assert.Equal(Elsewhere("user"), user.Path);
        Assert.Equal(LocationSource.ConfigFile, user.Source);

        _env.Variables["PIP_CACHE_DIR"] = Elsewhere("env");
        Assert.Equal(Elsewhere("env"), Resolve("pipCache").Path);
    }

    [Fact]
    public void NuGet_packages_from_nuget_config_resolve_relative_values_against_the_file()
    {
        string config = Path.Join(Roaming, "NuGet", "NuGet.Config");
        _env.Files[config] = "<?xml version=\"1.0\"?><configuration><config><add key=\"globalPackagesFolder\" value=\"..\\..\\pkgs\" /></config></configuration>";
        _env.Files[config] = _env.Files[config].Replace('\\', Path.DirectorySeparatorChar);

        ResolvedLocation packages = Resolve("nugetPackages");

        Assert.Equal(Path.GetFullPath(Path.Join(Roaming, "NuGet", "..", "..", "pkgs")), packages.Path);
        Assert.Equal(LocationSource.ConfigFile, packages.Source);

        _env.Variables["NUGET_PACKAGES"] = Elsewhere("np");
        Assert.Equal(Elsewhere("np"), Resolve("nugetPackages").Path);
    }

    [Fact]
    public void NuGet_defaults_and_http_cache()
    {
        Assert.Equal(Path.Join(Profile, ".nuget", "packages"), Resolve("nugetPackages").Path);
        Assert.Equal(Path.Join(Local, "NuGet", "v3-cache"), Resolve("nugetHttpCache").Path);
        _env.Variables["NUGET_HTTP_CACHE_PATH"] = Elsewhere("http");
        Assert.Equal(Elsewhere("http"), Resolve("nugetHttpCache").Path);
    }

    [Fact]
    public void Gradle_system_property_beats_gradle_user_home()
    {
        Assert.Equal(Path.Join(Profile, ".gradle"), Resolve("gradleHome").Path);

        _env.Variables["GRADLE_USER_HOME"] = Elsewhere("gh");
        Assert.Equal(Elsewhere("gh"), Resolve("gradleHome").Path);

        _env.Variables["GRADLE_OPTS"] = $"-Xmx2g \"-Dgradle.user.home={Elsewhere("opts dir")}\" -Dother=1";
        ResolvedLocation opts = Resolve("gradleHome");
        Assert.Equal(Elsewhere("opts dir"), opts.Path);
        Assert.Contains("GRADLE_OPTS", opts.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Go_module_cache_follows_go_env_rules_without_running_go()
    {
        Assert.Equal(Path.Join(Profile, "go", "pkg", "mod"), Resolve("goModCache").Path);

        _env.Files[Path.Join(Roaming, "go", "env")] = $"GOPATH={Elsewhere("envfile-gopath")}\n";
        Assert.Equal(Path.Join(Elsewhere("envfile-gopath"), "pkg", "mod"), Resolve("goModCache").Path);

        _env.Variables["GOPATH"] = $"{Elsewhere("first")};{Elsewhere("second")}";
        Assert.Equal(Path.Join(Elsewhere("first"), "pkg", "mod"), Resolve("goModCache").Path);

        _env.Files[Path.Join(Roaming, "go", "env")] = $"GOMODCACHE={Elsewhere("modcache")}\n";
        Assert.Equal(Elsewhere("modcache"), Resolve("goModCache").Path);

        _env.Variables["GOMODCACHE"] = Elsewhere("env");
        Assert.Equal(Elsewhere("env"), Resolve("goModCache").Path);
    }

    [Fact]
    public void Maven_settings_local_repository_expands_user_home()
    {
        _env.Files[Path.Join(Profile, ".m2", "settings.xml")] =
            "<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"><localRepository>${user.home}" + Path.DirectorySeparatorChar + "repo</localRepository></settings>";

        ResolvedLocation maven = Resolve("mavenRepo");

        Assert.Equal(Path.Join(Profile, "repo"), maven.Path);
        Assert.Equal(LocationSource.ConfigFile, maven.Source);
    }

    [Fact]
    public void Android_sdk_variables_in_order_then_studio_default()
    {
        Assert.Equal(Path.Join(Local, "Android", "Sdk"), Resolve("androidSdk").Path);
        _env.Variables["ANDROID_SDK_ROOT"] = Elsewhere("root");
        Assert.Equal(Elsewhere("root"), Resolve("androidSdk").Path);
        _env.Variables["ANDROID_HOME"] = Elsewhere("home");
        Assert.Equal(Elsewhere("home"), Resolve("androidSdk").Path);
    }

    [Fact]
    public void Scoop_root_from_its_config_json_then_variable()
    {
        Assert.Equal(Path.Join(Profile, "scoop"), Resolve("scoop").Path);
        Assert.Equal(Path.Join(_env.Folders["ProgramData"], "scoop"), Resolve("scoopGlobal").Path);

        _env.Files[Path.Join(Profile, ".config", "scoop", "config.json")] =
            $"{{ \"root_path\": {Json(Elsewhere("scoop"))} }}";
        Assert.Equal(Elsewhere("scoop"), Resolve("scoop").Path);

        _env.Variables["SCOOP"] = Elsewhere("env");
        Assert.Equal(Elsewhere("env"), Resolve("scoop").Path);
    }

    [Fact]
    public void Other_tools_env_then_default()
    {
        Assert.Equal(Path.Join(Profile, ".cargo"), Resolve("cargoHome").Path);
        Assert.Equal(Path.Join(Local, "Yarn", "Cache"), Resolve("yarnCache").Path);
        Assert.Equal(Path.Join(Local, "pnpm", "store"), Resolve("pnpmStore").Path);
        Assert.Equal(Path.Join(Local, "uv", "cache"), Resolve("uvCache").Path);
        Assert.Equal(Path.Join(Profile, ".cache", "huggingface"), Resolve("hfHome").Path);
        Assert.Equal(Path.Join(Profile, ".cache", "huggingface", "hub"), Resolve("hfHubCache").Path);
        Assert.Equal(Path.Join(_env.Folders["ProgramData"], "chocolatey"), Resolve("chocolatey").Path);
        Assert.Equal(Path.Join(Local, "Microsoft", "WinGet", "Packages"), Resolve("wingetPackages").Path);

        _env.Variables["CARGO_HOME"] = Elsewhere("cargo");
        _env.Variables["HF_HOME"] = Elsewhere("hf");
        _env.Variables["ChocolateyInstall"] = Elsewhere("choco");
        _env.Variables["PNPM_HOME"] = Elsewhere("pnpm");
        Assert.Equal(Elsewhere("cargo"), Resolve("cargoHome").Path);
        Assert.Equal(Path.Join(Elsewhere("hf"), "hub"), Resolve("hfHubCache").Path);
        Assert.Equal(Elsewhere("choco"), Resolve("chocolatey").Path);
        Assert.Equal(Path.Join(Elsewhere("pnpm"), "store"), Resolve("pnpmStore").Path);
    }

    [Fact]
    public void Config_files_of_yarn_uv_pnpm_and_winget()
    {
        _env.Files[Path.Join(Profile, ".yarnrc")] = $"# yarn lockfile v1\ncache-folder {Elsewhere("yarn")}\n";
        _env.Files[Path.Join(Roaming, "uv", "uv.toml")] = $"cache-dir = '{Elsewhere("uv")}'  # mine\n[pip]\ncache-dir = \"no\"\n";
        _env.Files[Path.Join(Local, "pnpm", "config", "rc")] = $"store-dir={Elsewhere("pnpm")}\n";
        _env.Files[Path.Join(Local, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "settings.json")] =
            "{ // comment\n \"installBehavior\": { \"portablePackageUserRoot\": " + Json(Elsewhere("wg")) + ", }, }";

        Assert.Equal(Elsewhere("yarn"), Resolve("yarnCache").Path);
        Assert.Equal(Elsewhere("uv"), Resolve("uvCache").Path);
        Assert.Equal(Elsewhere("pnpm"), Resolve("pnpmStore").Path);
        Assert.Equal(Elsewhere("wg"), Resolve("wingetPackages").Path);
    }

    [Fact]
    public void Known_folders_and_temp()
    {
        ResolvedLocation documents = Resolve("Documents");
        Assert.Equal(_env.Folders["Documents"], documents.Path);
        Assert.Equal(LocationSource.KnownFolder, documents.Source);

        ResolvedLocation temp = Resolve("temp");
        Assert.Equal(_env.TempPath, temp.Path);

        _env.Folders.Remove("Downloads");
        ResolvedLocation missing = Resolve("Downloads");
        Assert.Null(missing.Path);
        Assert.Equal(LocationSource.NotFound, missing.Source);
    }

    [Fact]
    public void A_relative_variable_value_is_not_used()
    {
        _env.Variables["CARGO_HOME"] = "relative\\cargo";

        ResolvedLocation cargo = Resolve("cargoHome");

        Assert.Equal(Path.Join(Profile, ".cargo"), cargo.Path);
        Assert.Contains("CARGO_HOME is not a full path", cargo.Detail, StringComparison.Ordinal);
    }
}
