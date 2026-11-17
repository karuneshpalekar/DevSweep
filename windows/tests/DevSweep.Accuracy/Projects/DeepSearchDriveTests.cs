using System.Globalization;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Projects;

/// <summary>
/// "Find all repos on this PC" on a real NTFS volume: repositories planted in Downloads-, Desktop-, drive-root- and
/// Program-Files-style folders of a scratch tree are all found, the ones hidden behind <c>node_modules</c> are not, and the same
/// search over the whole system drive finishes with its numbers logged (folders, repositories, folders per second).
/// </summary>
[Collection(AloneOnTheVolume.Name)]
public sealed class DeepSearchDriveTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFixture _fixture = new("deep-drive");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    private string Plant(params string[] parts)
    {
        string path = _fixture.CreateDirectory(Path.Join(parts));
        Directory.CreateDirectory(Path.Join(path, ".git"));
        File.WriteAllText(Path.Join(path, ".git", "HEAD"), "ref: refs/heads/main\n");
        return path;
    }

    [Fact]
    public void Planted_repositories_are_found_where_they_are_and_dependency_folders_are_skipped()
    {
        string downloads = Plant("Downloads", "tool-main");
        string work = Plant("Work", "client", "app");
        string programFiles = Plant("Program Files", "Vendor", "src");
        string deep = Plant("a", "b", "c", "d", "e", "f", "g", "h", "deep");
        Plant("web", "node_modules", "left-pad");
        Plant("py", ".venv", "Lib", "site-packages", "pkg");

        DeepSearchResult result = DeepSearch.Run(new DeepSearchOptions { Roots = [_fixture.Root] }, Ct);

        string[] found = [.. result.Repos.Select(r => r.Path)];
        Assert.Contains(found, p => string.Equals(p, downloads, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, p => string.Equals(p, work, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, p => string.Equals(p, programFiles, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, p => string.Equals(p, deep, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, found.Length);
        Assert.True(result.Complete);
        output.WriteLine($"scratch tree: {result.FoldersVisited} folders, {result.Repos.Count} repos, {result.Pruned} pruned, {result.Elapsed.TotalMilliseconds:0} ms ({Elevation.Describe()})");
    }

    [Fact]
    public void Every_fixed_drive_is_searched_in_a_reasonable_time_and_the_numbers_are_logged()
    {
        IReadOnlyList<string> drives = DeepSearch.DriveRoots(includeRemovable: false);
        Assert.NotEmpty(drives);
        int repos = 0;
        foreach (string drive in drives)
        {
            var progress = new List<DeepSearchProgress>();
            DeepSearchResult result = DeepSearch.Run(
                new DeepSearchOptions { Roots = [drive], TimeBudget = TimeSpan.FromMinutes(12), Progress = new Progress<DeepSearchProgress>(progress.Add) }, Ct);
            repos += result.Repos.Count;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{drive}: {result.FoldersVisited:N0} folders in {result.Elapsed.TotalSeconds:0.0} s = {result.FoldersPerSecond:N0} folders/s; {result.Repos.Count} repositories; "
                + $"pruned {result.Pruned:N0} dependency/system folders; unreadable {result.Unreadable:N0}; links not followed {result.LinksNotFollowed:N0}; "
                + $"cloud-only {result.CloudOnly}; bare {result.BareRepos}; stopped: {result.Stopped}; {Elevation.Describe()}"));
            foreach (FoundRepo repo in result.Repos.Take(10))
            {
                output.WriteLine("  repo: " + repo.Path);
            }

            Assert.True(result.FoldersVisited > 100, "A drive has more than a hundred folders.");
            Assert.True(result.Complete, $"The search of {drive} did not finish within 12 minutes ({result.Stopped}).");
        }

        Assert.True(repos > 0, "The runner's own checkout is a repository on one of the drives.");
    }
}
