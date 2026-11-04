using System.Text;

namespace DevSweep.Fixtures;

/// <summary>
/// A throwaway folder under %TEMP% (a real NTFS volume on the CI runner).
/// Removes itself, undoing any ACL changes first. Deleting never follows
/// junctions: .NET removes the link, not its target.
/// </summary>
public sealed class TempFixture : IDisposable
{
    private readonly Stack<Action> _cleanups = new();

    public TempFixture(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), "DevSweepFixtures", $"{name}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public string CreateDirectory(string relative) => Directory.CreateDirectory(PathOf(relative)).FullName;

    /// <summary>A file of <paramref name="length"/> bytes of incompressible data.</summary>
    public string CreateFile(string relative, long length)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var random = new Random(relative.Length);
        byte[] buffer = new byte[64 * 1024];
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        for (long written = 0; written < length; written += buffer.Length)
        {
            random.NextBytes(buffer);
            stream.Write(buffer, 0, (int)Math.Min(buffer.Length, length - written));
        }

        return path;
    }

    /// <summary>A file of <paramref name="length"/> bytes of highly compressible text.</summary>
    public string CreateCompressibleFile(string relative, long length)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] line = Encoding.ASCII.GetBytes("DevSweep measures what the disk really holds. 0123456789\r\n");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        for (long written = 0; written < length; written += line.Length)
        {
            stream.Write(line, 0, (int)Math.Min(line.Length, length - written));
        }

        return path;
    }

    /// <summary>Runs <paramref name="cleanup"/> before the folder is deleted (last registered runs first).</summary>
    public void OnCleanup(Action cleanup) => _cleanups.Push(cleanup);

    public void Dispose()
    {
        while (_cleanups.TryPop(out Action? cleanup))
        {
            Try(cleanup);
        }

        Try(() => Directory.Delete(Root, recursive: true));
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder must not fail a test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
