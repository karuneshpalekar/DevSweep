using System.Diagnostics;
using System.Globalization;
using System.Text;
using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// The Mac app's Terminal script, for Windows: a PowerShell script that lists the
/// commands and waits for Enter before running anything, stops at the first failure,
/// and keeps the window open. When the commands need an administrator and the window
/// is not elevated, it explains that and offers to reopen itself "as administrator"
/// (Windows then asks the user); it never elevates by itself.
/// </summary>
public static class TerminalScript
{
    public static string Build(RuntimeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var s = new StringBuilder();
        string Q(string text) => PowerShellText.Quote(text);
        s.AppendLine(CultureInfo.InvariantCulture, $"# DevSweep: {OneLine(step.Title)}");
        s.AppendLine("# Written by DevSweep. Nothing runs until you press Enter in the window.");
        s.AppendLine("$ErrorActionPreference = 'Stop'");
        s.AppendLine(CultureInfo.InvariantCulture, $"$Host.UI.RawUI.WindowTitle = {Q("DevSweep: " + OneLine(step.Title))}");
        s.AppendLine("# PATH as a new terminal gets it (system, then user), so tools installed since this window's parent started are found.");
        s.AppendLine("$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')");
        s.AppendLine("Clear-Host");
        s.AppendLine(CultureInfo.InvariantCulture, $"Write-Host {Q("DevSweep: " + OneLine(step.Title))}");
        s.AppendLine("Write-Host ''");
        s.AppendLine(CultureInfo.InvariantCulture, $"Write-Host {Q(OneLine(step.Detail))}");
        s.AppendLine("Write-Host ''");
        s.AppendLine("Write-Host 'These commands will run:'");
        foreach (string command in step.Commands)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Write-Host {Q("  > " + OneLine(command))}");
        }

        s.AppendLine("Write-Host ''");
        if (step.NeedsAdmin)
        {
            s.AppendLine("$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
            s.AppendLine("if (-not $isAdmin) {");
            s.AppendLine("    Write-Host 'These commands change things for every user of this PC, so they need an administrator.'");
            s.AppendLine("    Write-Host 'Type A and press Enter to open this script in a new window as administrator (Windows will ask you first).'");
            s.AppendLine("    Write-Host 'Close this window to cancel. Nothing has been changed.'");
            s.AppendLine("    $answer = Read-Host 'A to run as administrator'");
            s.AppendLine("    if ($answer -eq 'A') { Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', ('\"' + $PSCommandPath + '\"') }");
            s.AppendLine("    return");
            s.AppendLine("}");
        }

        s.AppendLine("$null = Read-Host 'Press Enter to run them, or close this window to cancel'");
        s.AppendLine("try {");
        foreach (string command in step.Commands)
        {
            s.AppendLine("    $global:LASTEXITCODE = 0");
            s.AppendLine(CultureInfo.InvariantCulture, $"    Write-Host {Q("> " + OneLine(command))}");
            s.AppendLine(CultureInfo.InvariantCulture, $"    {OneLine(command)}");
            s.AppendLine("    if ($LASTEXITCODE -ne 0) { throw \"The command above failed (exit code $LASTEXITCODE). Nothing after it ran.\" }");
        }

        s.AppendLine("    Write-Host ''");
        s.AppendLine("    Write-Host 'Finished. You can close this window, then check again in DevSweep.'");
        s.AppendLine("} catch {");
        s.AppendLine("    Write-Host $_ -ForegroundColor Red");
        s.AppendLine("}");
        return s.ToString();
    }

    /// <summary>Writes the script to <c>%TEMP%\DevSweep\Scripts</c> (UTF-8 with BOM, which Windows PowerShell needs) and returns its path.</summary>
    public static string Write(RuntimeStep step, string tempDirectory)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(tempDirectory);
        string folder = System.IO.Path.Join(tempDirectory, "DevSweep", "Scripts");
        Directory.CreateDirectory(folder);
        string safeId = new(step.Id.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        string path = System.IO.Path.Join(folder, string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeId}.ps1"));
        File.WriteAllText(path, Build(step), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>
    /// Opens the script in a new, visible PowerShell window (Windows Terminal when it is the
    /// default terminal), not elevated, without the user's profile.
    /// </summary>
    public static Process? Open(string scriptPath, string? systemRoot = null)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        string windows = systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string powershell = System.IO.Path.Join(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = true,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{scriptPath}\"",
            WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath) ?? windows,
        };
        return Process.Start(start);
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
