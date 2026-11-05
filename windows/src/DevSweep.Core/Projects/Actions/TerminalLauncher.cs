using System.Diagnostics;
using System.Globalization;
using System.Text;
using DevSweep.Core.Projects.Git;

namespace DevSweep.Core.Projects.Actions;

/// <summary>One command a terminal script runs: what is shown, and the PowerShell that runs it.</summary>
/// <param name="Shown">The command as the user reads it (never a token).</param>
/// <param name="PowerShell">The PowerShell statement(s).</param>
public sealed record TerminalCommand(string Shown, string PowerShell);

/// <summary>A visible terminal job: a title, what it does, and its commands.</summary>
public sealed record TerminalJob(string Id, string Title, string Detail, IReadOnlyList<TerminalCommand> Commands);

/// <summary>How the window was opened.</summary>
/// <param name="Argv">The program and arguments started (Windows Terminal, else Windows PowerShell).</param>
/// <param name="ScriptPath">The script it runs.</param>
public sealed record TerminalLaunch(IReadOnlyList<string> Argv, string ScriptPath, bool WindowsTerminal);

/// <summary>
/// Push and sign-in run where the user can see them (the Mac app uses Terminal): a PowerShell
/// script that lists every command, waits for Enter, runs them one by one showing each, stops at
/// the first failure and stays open. It opens in Windows Terminal when installed, else in a
/// Windows PowerShell window; never elevated. Tokens are fetched inside the window by
/// <c>gh auth token</c> into a variable of that window only; they are never written to the script.
/// </summary>
public static class TerminalLauncher
{
    public static string Quote(string text) => "'" + (text ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Pushes the current branch as <paramref name="account"/> (the Mac Push: <c>git push || git push -u origin HEAD</c>).</summary>
    public static TerminalJob Push(string folder, string name, string git, string? gh, string? account, int unpushed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(git);
        var commands = new List<TerminalCommand> { new($"cd {folder}", $"Set-Location -LiteralPath {Quote(folder)}") };
        string credentials = string.Empty;
        if (gh is not null && account is not null)
        {
            commands.Add(new TerminalCommand(
                $"$env:GH_TOKEN = gh auth token --user {account}   (this window only; your active gh account doesn't change)",
                $"$env:GH_TOKEN = (& {Quote(gh)} auth token --hostname github.com --user {Quote(account)})"));
            credentials = string.Join(' ', GitCredentials.Config(gh).Select(c => "-c " + Quote(c))) + " ";
        }

        commands.Add(new TerminalCommand(
            "git push   (or, for a branch with no upstream yet, git push -u origin HEAD)",
            $"& {Quote(git)} {credentials}push; if ($LASTEXITCODE -ne 0) {{ & {Quote(git)} {credentials}push -u origin HEAD }}"));
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"Pushes the current branch of {name} to GitHub{(account is null ? string.Empty : " as " + account)}. Other branches aren't pushed. {Project.Plural(unpushed, "commit")} not on GitHub before.");
        return new TerminalJob("push-" + name, $"Push {name}", detail, commands);
    }

    /// <summary>
    /// GitHub's sign-in (<c>gh auth login</c>, browser flow). gh makes a newly added account the
    /// active one, so when another account was active it is switched back right after, and the
    /// account active in your terminal stays the same.
    /// </summary>
    public static TerminalJob SignIn(string gh, string? activeBefore)
    {
        ArgumentNullException.ThrowIfNull(gh);
        var commands = new List<TerminalCommand>
        {
            new("gh auth login --hostname github.com --git-protocol https --web",
                $"& {Quote(gh)} auth login --hostname github.com --git-protocol https --web"),
        };
        if (activeBefore is not null)
        {
            commands.Add(new TerminalCommand(
                $"gh auth switch --hostname github.com --user {activeBefore}   (keeps {activeBefore} active, as before)",
                $"& {Quote(gh)} auth switch --hostname github.com --user {Quote(activeBefore)}"));
        }

        return new TerminalJob("sign-in", "Sign in to GitHub", "Sign in through the browser. When you're done, come back to DevSweep and press Refresh.", commands);
    }

    public static string Script(TerminalJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"# DevSweep: {OneLine(job.Title)}");
        s.AppendLine("# Written by DevSweep. Nothing runs until you press Enter in the window.");
        s.AppendLine(CultureInfo.InvariantCulture, $"$Host.UI.RawUI.WindowTitle = {Quote("DevSweep: " + OneLine(job.Title))}");
        s.AppendLine(CultureInfo.InvariantCulture, $"Write-Host {Quote("DevSweep: " + OneLine(job.Title))}");
        s.AppendLine(CultureInfo.InvariantCulture, $"Write-Host {Quote(OneLine(job.Detail))}");
        s.AppendLine("Write-Host ''");
        s.AppendLine("Write-Host 'These commands will run:'");
        foreach (TerminalCommand c in job.Commands)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Write-Host {Quote("  > " + OneLine(c.Shown))}");
        }

        s.AppendLine("Write-Host ''");
        s.AppendLine("$null = Read-Host 'Press Enter to run them, or close this window to cancel'");
        s.AppendLine("try {");
        foreach (TerminalCommand c in job.Commands)
        {
            s.AppendLine("    $global:LASTEXITCODE = 0");
            s.AppendLine(CultureInfo.InvariantCulture, $"    Write-Host {Quote("> " + OneLine(c.Shown))}");
            s.AppendLine(CultureInfo.InvariantCulture, $"    {OneLine(c.PowerShell)}");
            s.AppendLine("    if ($LASTEXITCODE -ne 0) { throw \"The command above failed (exit code $LASTEXITCODE). Nothing after it ran.\" }");
        }

        s.AppendLine("    Write-Host ''");
        s.AppendLine("    Write-Host 'Finished. You can close this window, then press Refresh in DevSweep.'");
        s.AppendLine("} catch {");
        s.AppendLine("    Write-Host $_ -ForegroundColor Red");
        s.AppendLine("} finally {");
        s.AppendLine("    Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue");
        s.AppendLine("}");
        return s.ToString();
    }

    /// <summary>Writes the script to <c>%TEMP%\DevSweep\Scripts</c> (UTF-8 with BOM, which Windows PowerShell needs).</summary>
    public static string WriteScript(TerminalJob job, string? tempDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        string folder = Path.Join(tempDirectory ?? Path.GetTempPath(), "DevSweep", "Scripts");
        Directory.CreateDirectory(folder);
        string safe = new(job.Id.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        string path = Path.Join(folder, string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}.ps1"));
        File.WriteAllText(path, Script(job), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>The window to open: Windows Terminal (<c>wt.exe</c>, on PATH when installed) running PowerShell, else PowerShell's own window.</summary>
    public static TerminalLaunch Plan(string scriptPath, string? windowsTerminal, string? systemRoot = null)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        string windows = systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string powershell = Path.Join(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        string[] ps = [powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", scriptPath];
        return windowsTerminal is not null
            ? new TerminalLaunch([windowsTerminal, "new-tab", "--title", "DevSweep", .. ps], scriptPath, true)
            : new TerminalLaunch(ps, scriptPath, false);
    }

    /// <summary>Writes the script and opens the window (not elevated).</summary>
    public static TerminalLaunch Open(TerminalJob job)
    {
        string script = WriteScript(job);
        TerminalLaunch launch = Plan(script, ToolLocator.Find("wt"));
        var start = new ProcessStartInfo(launch.Argv[0])
        {
            // PowerShell on its own needs the shell to give it a new console window; wt makes its own.
            UseShellExecute = !launch.WindowsTerminal,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Path.GetTempPath(),
        };
        foreach (string argument in launch.Argv.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start);
        return launch;
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
