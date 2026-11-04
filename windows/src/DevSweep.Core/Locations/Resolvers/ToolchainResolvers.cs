namespace DevSweep.Core.Locations.Resolvers;

/// <summary>
/// <c>{gradleHome}</c>: <c>-Dgradle.user.home=</c> in <c>GRADLE_OPTS</c> (a system
/// property beats the variable), <c>GRADLE_USER_HOME</c>, then <c>~\.gradle</c>.
/// </summary>
public sealed class GradleHomeResolver : LocationResolver
{
    public override string Name => "gradleHome";

    public override string Description => "Gradle user home";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return SystemProperty(trail, "GRADLE_OPTS", "gradle.user.home")
            ?? trail.Variable("GRADLE_USER_HOME")
            ?? trail.Default("Profile", ".gradle");
    }

    /// <summary><c>-Dname=value</c> (value optionally quoted) in a JVM options variable.</summary>
    internal static ResolvedLocation? SystemProperty(ResolutionTrail trail, string variable, string property)
    {
        string? options = trail.Environment.GetVariable(variable);
        string marker = $"-D{property}=";
        int start = options?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (options is null || start < 0)
        {
            trail.Looked($"no -D{property} in {variable}");
            return null;
        }

        // -Dname="C:\a b", "-Dname=C:\a b" and -Dname=C:\ab are all in use.
        string rest = options[(start + marker.Length)..];
        bool wholeQuoted = start > 0 && options[start - 1] == '"';
        string value = rest.StartsWith('"') && rest.IndexOf('"', 1) is int close and > 0 ? rest[1..close]
            : wholeQuoted && rest.IndexOf('"', StringComparison.Ordinal) is int end and >= 0 ? rest[..end]
            : rest.Split(' ', 2)[0];
        if (!Path.IsPathFullyQualified(value))
        {
            trail.Looked($"-D{property} in {variable} is not a full path");
            return null;
        }

        return trail.Found(value, LocationSource.Environment, $"{variable} (-D{property})");
    }
}

/// <summary><c>{mavenRepo}</c>: <c>-Dmaven.repo.local=</c> in <c>MAVEN_OPTS</c>, <c>localRepository</c> in <c>~\.m2\settings.xml</c>, then in <c>%MAVEN_HOME%\conf\settings.xml</c>, then <c>~\.m2\repository</c>.</summary>
public sealed class MavenRepositoryResolver : LocationResolver
{
    public override string Name => "mavenRepo";

    public override string Description => "Maven local repository";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        static string? Read(string text) => ConfigText.XmlElementText(text, "localRepository");
        string? mavenHome = trail.Environment.GetVariable("MAVEN_HOME") ?? trail.Environment.GetVariable("M2_HOME");
        return GradleHomeResolver.SystemProperty(trail, "MAVEN_OPTS", "maven.repo.local")
            ?? trail.Config(trail.Under("Profile", ".m2", "settings.xml"), "localRepository", Read)
            ?? trail.Config(mavenHome is null ? null : Path.Join(mavenHome, "conf", "settings.xml"), "localRepository", Read)
            ?? trail.Default("Profile", ".m2", "repository");
    }
}

/// <summary><c>{cargoHome}</c>: <c>CARGO_HOME</c>, then <c>~\.cargo</c>.</summary>
public sealed class CargoHomeResolver : LocationResolver
{
    public override string Name => "cargoHome";

    public override string Description => "Cargo home";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("CARGO_HOME") ?? trail.Default("Profile", ".cargo");
    }
}

/// <summary>
/// <c>{goModCache}</c>, the way <c>go env GOMODCACHE</c> works it out but without
/// running go: <c>GOMODCACHE</c>; <c>GOMODCACHE</c> in the go env file (<c>GOENV</c>
/// or <c>%APPDATA%\go\env</c>); the first <c>GOPATH</c> entry (variable, then env
/// file) plus <c>pkg\mod</c>; then <c>~\go\pkg\mod</c>.
/// </summary>
public sealed class GoModCacheResolver : LocationResolver
{
    public override string Name => "goModCache";

    public override string Description => "Go module cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        string? envFile = trail.Environment.GetVariable("GOENV") ?? trail.Under("RoamingAppData", "go", "env");
        return trail.Variable("GOMODCACHE")
            ?? trail.Config(envFile, "GOMODCACHE", text => ConfigText.KeyValue(text, "GOMODCACHE"))
            ?? FirstGoPath(trail, trail.Environment.GetVariable("GOPATH"), LocationSource.Environment, "GOPATH")
            ?? FirstGoPath(trail, envFile is null ? null : trail.Environment.ReadAllText(envFile) is { } text ? ConfigText.KeyValue(text, "GOPATH") : null,
                LocationSource.ConfigFile, $"GOPATH in {envFile}")
            ?? trail.Default("Profile", "go", "pkg", "mod");
    }

    private static ResolvedLocation? FirstGoPath(ResolutionTrail trail, string? goPath, LocationSource source, string detail)
    {
        string? first = goPath?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null || !Path.IsPathFullyQualified(first))
        {
            trail.Looked($"no {detail}");
            return null;
        }

        return trail.Found(Path.Join(first, "pkg", "mod"), source, detail);
    }
}

/// <summary><c>{androidSdk}</c>: <c>ANDROID_HOME</c>, <c>ANDROID_SDK_ROOT</c>, then Android Studio's default <c>%LOCALAPPDATA%\Android\Sdk</c>.</summary>
public sealed class AndroidSdkResolver : LocationResolver
{
    public override string Name => "androidSdk";

    public override string Description => "Android SDK";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("ANDROID_HOME") ?? trail.Variable("ANDROID_SDK_ROOT") ?? trail.Default("LocalAppData", "Android", "Sdk");
    }
}

/// <summary><c>{hfHome}</c>: <c>HF_HOME</c>, <c>XDG_CACHE_HOME\huggingface</c>, then <c>~\.cache\huggingface</c>.</summary>
public sealed class HuggingFaceHomeResolver : LocationResolver
{
    public override string Name => "hfHome";

    public override string Description => "Hugging Face home";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("HF_HOME") ?? trail.Variable("XDG_CACHE_HOME", "huggingface") ?? trail.Default("Profile", ".cache", "huggingface");
    }
}

/// <summary><c>{hfHubCache}</c>: <c>HF_HUB_CACHE</c>, <c>HUGGINGFACE_HUB_CACHE</c>, then <c>{hfHome}\hub</c>.</summary>
public sealed class HuggingFaceHubCacheResolver : LocationResolver
{
    public override string Name => "hfHubCache";

    public override string Description => "Hugging Face model cache";

    protected override ResolvedLocation? Resolve(ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return trail.Variable("HF_HUB_CACHE") ?? trail.Variable("HUGGINGFACE_HUB_CACHE") ?? trail.Default("hfHome", "hub");
    }
}
