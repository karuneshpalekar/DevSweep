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

    public static Task<CommandResult> RunAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken = default)
        => RunAsync(argv, timeout, CommandOptions.Default, cancellationToken);

    /// <summary>Runs with extra environment variables and a different output cap (M3c: git and gh).</summary>
    public static async Task<CommandResult> RunAsync(IReadOnlyList<string> argv, TimeSpan timeout, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(options);
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

        foreach ((string name, string? value) in options.Environment)
        {
            start.Environment[name] = value;
        }

        if (options.WorkingDirectory is { } directory)
        {
            start.WorkingDirectory = directory;
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
        Task<string> stdout = ReadCappedAsync(process.StandardOutput, options.MaxCapturedChars);
        Task<string> stderr = ReadCappedAsync(process.StandardError, options.MaxCapturedChars);
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

    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxChars)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(read, maxChars - text.Length);
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

/// <summary>How <see cref="CommandRunner"/> starts a program, beyond its arguments.</summary>
public sealed record CommandOptions
{
    public static CommandOptions Default { get; } = new();

    /// <summary>Variables set (or, with a null value, removed) for the child only.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Output kept per stream; the rest is dropped and <c>[output truncated]</c> appended.</summary>
    public int MaxCapturedChars { get; init; } = CommandRunner.MaxCapturedChars;

    /// <summary>The child's working folder; null keeps this process's.</summary>
    public string? WorkingDirectory { get; init; }
}
