using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DevSweep.Core.Cleaning;

/// <summary>What running a command did.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Elapsed, string? StartError = null)
{
    public bool Succeeded => StartError is null && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a tool's own command: the program and each argument passed as given
/// (no shell, so nothing is re-parsed or expanded), output captured, killed
/// (with its child processes) when it runs over its time limit.
/// </summary>
public static class CommandRunner
{
    /// <summary>Output kept per stream; the rest is dropped (with a note).</summary>
    public const int MaxCapturedChars = 64 * 1024;

    public static async Task<CommandResult> RunAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
        {
            return new CommandResult(-1, string.Empty, string.Empty, false, TimeSpan.Zero, "No command to run.");
        }

        var start = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in argv.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new CommandResult(-1, string.Empty, string.Empty, false, clock.Elapsed, $"{argv[0]} could not be started: {ex.Message}");
        }

        process.StandardInput.Close();
        Task<string> stdout = ReadCappedAsync(process.StandardOutput);
        Task<string> stderr = ReadCappedAsync(process.StandardError);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new CommandResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            timedOut,
            clock.Elapsed);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(read, MaxCapturedChars - text.Length);
            if (keep > 0)
            {
                text.Append(buffer, 0, keep);
            }

            truncated |= keep < read;
        }

        if (truncated)
        {
            text.Append("\n[output truncated]");
        }

        return text.ToString();
    }
}
