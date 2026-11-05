using System.Diagnostics;
using DevSweep.Core.Projects.Git;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Projects;

/// <summary>
/// Real git repositories for the Projects accuracy tests, built under %TEMP% with the git on
/// the runner: a local bare repository as <c>origin</c> (so "pushed" is real without a
/// network), a fixed commit identity in the environment, and <c>core.autocrlf=false</c> in
/// each repository unless a test sets it.
/// </summary>
public sealed class GitFixture : IDisposable
{
    private static readonly string? Program = ToolLocator.Git();

    public GitFixture(string name) => Temp = new TempFixture(name);

    public TempFixture Temp { get; }

    public string Root => Temp.Root;

    public static string? SkipReason => Program is null ? "git is not installed on this machine" : null;

    public void Dispose() => Temp.Dispose();

    /// <summary>Runs git and returns stdout; fails the test when git fails (unless <paramref name="allowFailure"/>).</summary>
    public static string Git(string? directory, params string[] arguments) => Run(directory, false, arguments).Output;

    public static (int Exit, string Output) Run(string? directory, bool allowFailure, params string[] arguments)
    {
        Assert.NotNull(Program);
        var start = new ProcessStartInfo(Program!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory is not null && Directory.Exists(directory) ? directory : Path.GetTempPath(),
        };
        foreach (string setting in (string[])["init.defaultBranch=main", "protocol.file.allow=always", "core.longpaths=true"])
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }

        if (directory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_AUTHOR_NAME"] = "Fixture";
        start.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.com";
        start.Environment["GIT_COMMITTER_NAME"] = "Fixture";
        start.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.com";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (!allowFailure)
        {
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr.Result}");
        }

        return (process.ExitCode, stdout.Result);
    }

    /// <summary>A bare repository to push to.</summary>
    public string Bare(string name)
    {
        string bare = Path.Join(Root, name + ".git");
        Git(null, "init", "-q", "--bare", "-b", "main", bare);
        return bare;
    }

    /// <summary>A repository with one commit; with <paramref name="pushed"/>, an origin (a bare repository) that has it.</summary>
    public string Repo(string name, bool pushed = true, string? originUrl = null)
    {
        string work = Path.Join(Root, name);
        Git(null, "init", "-q", "-b", "main", work);
        Git(work, "config", "core.autocrlf", "false");
        Commit(work, "a.txt", "first\n");
        if (pushed)
        {
            string bare = Bare("remote-" + name.Replace('/', '-').Replace('\\', '-'));
            Git(work, "remote", "add", "origin", bare);
            Git(work, "push", "-q", "-u", "origin", "main");
            if (originUrl is not null)
            {
                Git(work, "remote", "set-url", "origin", originUrl);
                Git(work, "remote", "set-url", "--push", "origin", bare);
            }
        }
        else if (originUrl is not null)
        {
            Git(work, "remote", "add", "origin", originUrl);
        }

        return work;
    }

    public static void Commit(string work, string file, string text = "x\n")
    {
        string path = Path.Join(work, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", "add " + file);
    }

    public static Task<RemovalSafety> Check(string folder, SafetyCheckOptions? options = null)
        => new SafetyChecker(GitCli.Default).CheckAsync(folder, options);
}
