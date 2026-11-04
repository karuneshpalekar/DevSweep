using System.Diagnostics;

namespace DevSweep.Fixtures;

/// <summary>
/// A child process (Windows PowerShell) that holds a file open with no sharing,
/// like an editor or a running tool would, until disposed.
/// </summary>
public sealed class FileHolder : IDisposable
{
    private readonly Process _process;

    private FileHolder(Process process) => _process = process;

    public int ProcessId => _process.Id;

    /// <exception cref="TimeoutException">The child did not report that it holds the file.</exception>
    public static FileHolder Start(string path)
    {
        string quoted = path.Replace("'", "''", StringComparison.Ordinal);
        string script = $"$f = [IO.File]::Open('{quoted}', 'Open', 'ReadWrite', 'None'); [Console]::Out.WriteLine('holding'); [Console]::Out.Flush(); Start-Sleep -Seconds 600";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in (string[])["-NoProfile", "-NonInteractive", "-Command", script])
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new IOException("Cannot start powershell.exe");
        var holder = new FileHolder(process);
        Task<string?> line = process.StandardOutput.ReadLineAsync();
        if (!line.Wait(TimeSpan.FromSeconds(60)) || line.Result != "holding")
        {
            string error = process.HasExited ? process.StandardError.ReadToEnd() : "(still running)";
            holder.Dispose();
            throw new TimeoutException($"The child process did not open {path}: {line.Status} {error}");
        }

        return holder;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }

        _process.Dispose();
    }
}
