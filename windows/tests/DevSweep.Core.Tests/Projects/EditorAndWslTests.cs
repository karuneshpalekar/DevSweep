using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Editors;
using Xunit;

namespace DevSweep.Core.Tests.Projects;

/// <summary>Editor detection against a fake file layout and registry, WSL name handling, other-host and credential-free origins.</summary>
public sealed class EditorAndWslTests
{
    private sealed class FakeMachine : IEditorEnvironment
    {
        public Dictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = @"C:\Users\a\AppData\Local",
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        };

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> AppPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? VsWherePath { get; set; }

        public string? Variable(string name) => Vars.TryGetValue(name, out string? v) ? v : null;

        public bool FileExists(string path) => Files.Contains(path);

        public IReadOnlyList<string> SubFolders(string parent)
        {
            string prefix = parent.EndsWith('\\') ? parent : parent + "\\";
            return [.. Files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(f => f[prefix.Length..].Split('\\')).Where(p => p.Length > 1).Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        public string? AppPath(string exe) => AppPaths.TryGetValue(exe, out string? p) ? p : null;

        public string? VsWhere() => VsWherePath;
    }

    [Fact]
    public void Nothing_installed_finds_nothing_and_falls_back_to_explorer()
    {
        IReadOnlyList<EditorInfo> found = EditorDetector.Detect(new FakeMachine());

        Assert.Empty(found);
        (string program, IReadOnlyList<string> args) = EditorDetector.OpenCommand(EditorDetector.Choose(found, "vscode"), @"D:\code\x");
        Assert.EndsWith("explorer.exe", program, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([@"D:\code\x"], args);
    }

    [Fact]
    public void Finds_each_editor_in_its_usual_place()
    {
        var m = new FakeMachine();
        m.Files.Add(@"C:\Users\a\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        m.Files.Add(@"C:\Users\a\AppData\Local\Programs\Microsoft VS Code Insiders\Code - Insiders.exe");
        m.Files.Add(@"C:\Users\a\AppData\Local\Programs\cursor\Cursor.exe");
        m.Files.Add(@"C:\Users\a\AppData\Local\Programs\Windsurf\Windsurf.exe");
        m.Files.Add(@"C:\Users\a\AppData\Local\JetBrains\Toolbox\apps\Rider\ch-0\241.1\bin\rider64.exe");
        m.Files.Add(@"C:\Users\a\AppData\Local\JetBrains\Toolbox\apps\Rider\ch-0\243.9\bin\rider64.exe");
        m.Files.Add(@"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe");

        IReadOnlyList<EditorInfo> found = EditorDetector.Detect(m);

        Assert.Equal(["vscode", "vscode-insiders", "visualstudio", "rider", "cursor", "windsurf"], found.Select(e => e.Id));
        Assert.Equal(@"C:\Users\a\AppData\Local\JetBrains\Toolbox\apps\Rider\ch-0\243.9\bin\rider64.exe", found.Single(e => e.Id == "rider").Path);
        Assert.EndsWith(@"2022\Community\Common7\IDE\devenv.exe", found.Single(e => e.Id == "visualstudio").Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Vswhere_wins_for_Visual_Studio_and_the_registry_finds_VS_Code_in_an_unusual_folder()
    {
        var m = new FakeMachine { VsWherePath = @"E:\VS\Common7\IDE\devenv.exe" };
        m.AppPaths["Code.exe"] = @"E:\Tools\Code\Code.exe";

        IReadOnlyList<EditorInfo> found = EditorDetector.Detect(m);

        Assert.Equal(@"E:\VS\Common7\IDE\devenv.exe", found.Single(e => e.Id == "visualstudio").Path);
        Assert.Equal(@"E:\Tools\Code\Code.exe", found.Single(e => e.Id == "vscode").Path);
    }

    [Fact]
    public void The_chosen_default_is_used_when_installed_else_the_first_found()
    {
        EditorInfo[] editors = [new("vscode", "Visual Studio Code", "a.exe"), new("rider", "JetBrains Rider", "b.exe")];

        Assert.Equal("rider", EditorDetector.Choose(editors, "rider")!.Id);
        Assert.Equal("vscode", EditorDetector.Choose(editors, "cursor")!.Id);
        Assert.Equal("vscode", EditorDetector.Choose(editors, null)!.Id);
        Assert.Null(EditorDetector.Choose([], "rider"));
        (string program, IReadOnlyList<string> args) = EditorDetector.OpenCommand(editors[1], @"C:\x y");
        Assert.Equal("b.exe", program);
        Assert.Equal([@"C:\x y"], args);
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\a\code", true, "Ubuntu")]
    [InlineData(@"\\wsl$\Debian\root\x", true, "Debian")]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu-22.04\home", true, "Ubuntu-22.04")]
    [InlineData(@"\\server\share\x", false, null)]
    [InlineData(@"C:\wsl.localhost\x", false, null)]
    public void Recognises_paths_inside_WSL(string path, bool wsl, string? distro)
    {
        Assert.Equal(wsl, WslDiscovery.IsWslPath(path));
        Assert.Equal(distro, WslDiscovery.DistroOf(path));
    }

    [Fact]
    public void Parses_wsl_output_even_when_it_is_utf16_read_as_bytes_of_text()
    {
        IReadOnlySet<string> names = WslDiscovery.ParseNames("U\0b\0u\0n\0t\0u\0\r\0\n\0Debian\r\n");

        Assert.Contains("Ubuntu", names);
        Assert.Contains("debian", names);
    }

    private sealed class FakeWsl(string[] registered, string[]? running) : IWslSource
    {
        public IReadOnlyList<string> RegisteredDistros() => registered;

        public Task<IReadOnlySet<string>?> RunningAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<string>?>(running?.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_stopped_distro_is_listed_but_marked_not_running_and_docker_distros_are_left_out()
    {
        IReadOnlyList<WslDistro> list = await WslDiscovery.ListAsync(new FakeWsl(["Ubuntu", "Debian", "docker-desktop", "docker-desktop-data"], ["Ubuntu"]), TestContext.Current.CancellationToken);

        Assert.Equal(["Ubuntu", "Debian"], list.Select(d => d.Name));
        Assert.True(list[0].Running);
        Assert.False(list[1].Running);
    }

    [Fact]
    public async Task When_wsl_cannot_be_asked_nothing_counts_as_running_so_nothing_is_started()
    {
        IReadOnlyList<WslDistro> list = await WslDiscovery.ListAsync(new FakeWsl(["Ubuntu"], null), TestContext.Current.CancellationToken);

        Assert.False(list.Single().Running);
        WslSearchResult result = await WslDiscovery.SearchAsync(new FakeWsl(["Ubuntu"], null), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(result.Repos);
    }

    [Fact]
    public void A_wsl_project_is_read_only_and_a_non_github_origin_is_an_other_host()
    {
        var wsl = new Project { Id = "local:x", Name = "x", NameWithOwner = "x", LocalPath = @"\\wsl.localhost\Ubuntu\home\a\x" };
        var gitlab = new Project { Id = "local:y", Name = "y", NameWithOwner = "y", LocalPath = @"C:\y", OriginUrl = "https://gitlab.com/g/y.git" };
        var local = new Project { Id = "local:z", Name = "z", NameWithOwner = "z", LocalPath = @"C:\z", OriginUrl = @"D:\bare\z.git" };

        Assert.True(wsl.IsWsl);
        Assert.Equal("In WSL, read-only", wsl.StatusText);
        Assert.Equal("gitlab.com", gitlab.OtherHost);
        Assert.Null(local.OtherHost);
    }

    [Theory]
    [InlineData("https://user:ghp_secret@github.com/o/r.git", "https://github.com/o/r.git")]
    [InlineData("https://github.com/o/r.git", "https://github.com/o/r.git")]
    [InlineData("git@github.com:o/r.git", "git@github.com:o/r.git")]
    [InlineData("ssh://git@host/o/r.git", "ssh://git@host/o/r.git")]
    [InlineData("", null)]
    public void An_origin_is_shown_without_credentials(string url, string? expected)
        => Assert.Equal(expected, ProjectMerger.Redact(url));
}
