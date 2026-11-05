using DevSweep.Core.Health.Runtimes;
using Xunit;

namespace DevSweep.Core.Tests.Health;

public sealed class ProjectRequirementTests
{
    [Fact]
    public void Every_requirement_file_is_read()
    {
        using var machine = new HealthMachine();
        string root = Path.Join(machine.Profile, "repos");
        string p = Path.Join(root, "everything");
        machine.File(Path.Join(p, ".nvmrc"), "v20.11.0\n");
        machine.File(Path.Join(p, "package.json"), """{ "name": "x", "engines": { "node": ">=18" } }""");
        machine.File(Path.Join(p, ".python-version"), "3.11.7\n");
        machine.File(Path.Join(p, "pyproject.toml"), "[project]\nrequires-python = \">=3.10\"\n");
        machine.File(Path.Join(p, ".ruby-version"), "ruby-3.2.2\n");
        machine.File(Path.Join(p, "go.mod"), "module x\n\ngo 1.22.1\n");
        machine.File(Path.Join(p, "build.gradle.kts"), "kotlin { jvmToolchain(17) }\n");
        machine.File(Path.Join(p, "composer.json"), """{"require":{"php":"^8.2|^8.3"}}""");
        machine.File(Path.Join(p, "rust-toolchain.toml"), "[toolchain]\nchannel = \"1.75.0\"\n");
        machine.File(Path.Join(p, "global.json"), "{ // pinned\n \"sdk\": { \"version\": \"8.0.100\", \"rollForward\": \"latestFeature\" } }");
        machine.File(Path.Join(p, ".tool-versions"), "nodejs 20.11.0\ngolang 1.22.1\n");
        machine.File(Path.Join(root, "group", "nested", ".nvmrc"), "lts/iron\n");
        machine.File(Path.Join(root, "group", "nested", "package.json"), "{}");
        machine.File(Path.Join(root, "node_modules", "dep", "package.json"), """{"engines":{"node":">=99"}}""");

        IReadOnlyList<string> projects = ProjectRequirementScanner.Projects([root]);
        Assert.Equal(["everything", "nested"], projects.Select(Path.GetFileName).Order(StringComparer.Ordinal));

        IReadOnlyList<RawRequirement> raw = ProjectRequirementScanner.Requirements(p);
        Assert.Equal(
            [".nvmrc node v20.11.0", ".python-version python 3.11.7", ".ruby-version ruby 3.2.2", ".tool-versions go 1.22.1", ".tool-versions node 20.11.0",
             "build.gradle.kts java 17", "composer.json php ^8.2", "global.json dotnet 8.0.100", "go.mod go >=1.22.1", "package.json node >=18",
             "pyproject.toml python >=3.10", "rust-toolchain.toml rust 1.75.0"],
            raw.Select(r => $"{r.File} {r.Tool} {r.Spec}").Order(StringComparer.Ordinal));
        Assert.Equal("latestFeature", raw.Single(r => r.Tool == "dotnet").RollForward);
        Assert.Empty(ProjectRequirementScanner.Requirements(Path.Join(root, "group", "nested")));
    }

    [Theory]
    [InlineData("8.0.104", "8.0.100", "latestPatch", true)]
    [InlineData("8.0.204", "8.0.100", "latestPatch", false)]
    [InlineData("8.0.204", "8.0.100", "latestFeature", true)]
    [InlineData("9.0.100", "8.0.100", "latestFeature", false)]
    [InlineData("9.0.100", "8.0.100", "latestMajor", true)]
    [InlineData("8.0.100", "8.0.100", "disable", true)]
    [InlineData("8.0.101", "8.0.100", "disable", false)]
    [InlineData("8.0.99", "8.0.100", "latestMajor", false)]
    public void Dotnet_sdks_satisfy_global_json_by_its_roll_forward_policy(string installed, string wanted, string rollForward, bool satisfies)
        => Assert.Equal(satisfies, ProjectRequirementScanner.DotnetSatisfies(installed, wanted, rollForward));

    [Fact]
    public void Requirements_are_checked_against_installs_and_support_dates()
    {
        Installation py(string v, bool def = false) => new() { Version = v, Cycle = DevSweep.Core.Health.Common.VersionText.MajorMinor(v), Path = "C:\\py" + v, Source = InstallSource.Pyenv, IsDefault = def };
        var reports = new List<RuntimeReport> { new() { Id = "python", Name = "Python", Installs = [py("3.12.1", def: true), py("3.11.7")] } };
        var cycles = new Dictionary<string, IReadOnlyList<ReleaseCycle>> { ["python"] = EolFixtures.Cycles(EolFixtures.Python) };
        var names = new Dictionary<string, string> { ["python"] = "Python" };
        RawRequirement[] raw =
        [
            new(@"C:\src\a", "python", "3.11.7", ".python-version"),
            new(@"C:\src\b", "python", ">=3.13", "pyproject.toml"),
            new(@"C:\src\c", "python", "3.9", ".python-version"),
            new(@"C:\src\d", "python", "^3.12", "pyproject.toml"),
        ];

        IReadOnlyList<ProjectRequirement> result = ProjectRequirementScanner.Evaluate(raw, reports, cycles, names, EolFixtures.Today,
            (tool, cycle, via) => FixCommands.Install(tool, cycle, via)?.Commands);

        Assert.Equal(RequirementStatus.Ok, result.Single(r => r.Project == "a").Status);
        Assert.Contains("isn't the one a new terminal runs", result.Single(r => r.Project == "a").Message, StringComparison.Ordinal);
        ProjectRequirement b = result.Single(r => r.Project == "b");
        Assert.Equal(RequirementStatus.NotInstalled, b.Status);
        Assert.Equal("b asks for Python >=3.13 (pyproject.toml), but you have 3.11.7 and 3.12.1.", b.Message);
        Assert.Equal(["pyenv install '3.13'", "pyenv global '3.13'"], b.Fix);
        Assert.Equal(RequirementStatus.EndOfLife, result.Single(r => r.Project == "c").Status);
        Assert.Contains("and is the one a new terminal runs", result.Single(r => r.Project == "d").Message, StringComparison.Ordinal);
        Assert.NotEqual(RequirementStatus.Ok, result[0].Status);
    }
}
