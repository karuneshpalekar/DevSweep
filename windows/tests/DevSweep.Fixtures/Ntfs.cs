using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace DevSweep.Fixtures;

/// <summary>Creates NTFS edge cases: hard links, sparse and compressed files, junctions, symlinks, denied folders.</summary>
public static unsafe class Ntfs
{
    public static void CreateHardLink(string link, string existing)
    {
        if (!PInvoke.CreateHardLink(link, existing))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateHardLink {link} -> {existing}");
        }
    }

    /// <summary>Marks a file sparse (FSCTL_SET_SPARSE).</summary>
    public static void MakeSparse(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        Control(handle, PInvoke.FSCTL_SET_SPARSE, ReadOnlySpan<byte>.Empty, path);
    }

    /// <summary>Deallocates a range of a sparse file (FSCTL_SET_ZERO_DATA).</summary>
    public static void ZeroRange(string path, long offset, long length)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        var range = new FILE_ZERO_DATA_INFORMATION { FileOffset = offset, BeyondFinalZero = offset + length };
        Control(handle, PInvoke.FSCTL_SET_ZERO_DATA, new ReadOnlySpan<byte>(&range, sizeof(FILE_ZERO_DATA_INFORMATION)), path);
    }

    /// <summary>Turns on NTFS (LZNT1) compression for a file, compressing its existing data (FSCTL_SET_COMPRESSION).</summary>
    public static void Compress(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        ushort format = (ushort)COMPRESSION_FORMAT.COMPRESSION_FORMAT_DEFAULT;
        Control(handle, PInvoke.FSCTL_SET_COMPRESSION, new ReadOnlySpan<byte>(&format, sizeof(ushort)), path);
    }

    /// <summary>Reference on-disk size from GetCompressedFileSizeW (compressed or sparse files).</summary>
    public static long CompressedFileSize(string path)
    {
        uint low = PInvoke.GetCompressedFileSize(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0)
        {
            throw new Win32Exception(error, $"GetCompressedFileSize {path}");
        }

        return ((long)high << 32) | low;
    }

    /// <summary>Creates a junction with <c>mklink /J</c> (no privileges needed).</summary>
    public static void CreateJunction(string link, string target)
    {
        (int exit, string output) = Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");
        if (exit != 0)
        {
            throw new IOException($"mklink /J failed ({exit}): {output}");
        }
    }

    /// <summary>Creates a directory symlink; false when the account may not (no Developer Mode or privilege).</summary>
    public static bool TryCreateDirectorySymlink(string link, string target)
        => TryLink(() => Directory.CreateSymbolicLink(link, target));

    /// <summary>Creates a file symlink; false when the account may not.</summary>
    public static bool TryCreateFileSymlink(string link, string target)
        => TryLink(() => File.CreateSymbolicLink(link, target));

    /// <summary>Compresses a file with the Windows Overlay Filter (<c>compact /exe:xpress4k</c>); false if unavailable.</summary>
    public static bool TryWofCompress(string path)
    {
        (int exit, _) = Run("compact.exe", $"/c /exe:xpress4k \"{path}\"");
        return exit == 0;
    }

    /// <summary>Denies listing <paramref name="directory"/> to Everyone until the returned scope is disposed.</summary>
    public static IDisposable DenyListing(string directory)
    {
        (int exit, string output) = Run("icacls.exe", $"\"{directory}\" /deny *S-1-1-0:(RD)");
        if (exit != 0)
        {
            throw new IOException($"icacls /deny failed ({exit}): {output}");
        }

        return new Scope(() => Run("icacls.exe", $"\"{directory}\" /remove:d *S-1-1-0"));
    }

    /// <summary>The 8.3 short form of an existing path (GetShortPathNameW); the path itself when it has none.</summary>
    public static string ShortPath(string path)
    {
        Span<char> buffer = new char[path.Length + 16];
        uint length = PInvoke.GetShortPathName(path, buffer);
        return length == 0 || length > buffer.Length ? path : buffer[..(int)length].ToString();
    }

    /// <summary>Gives <paramref name="path"/> the short name <paramref name="shortName"/> (fsutil, needs an administrator and 8.3 names enabled on the volume).</summary>
    public static bool TrySetShortName(string path, string shortName)
    {
        (int exit, _) = Run("fsutil.exe", $"file setshortname \"{path}\" {shortName}");
        return exit == 0;
    }

    public static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new IOException($"Cannot start {fileName}");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }

    private static void Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, string path)
    {
        if (!PInvoke.DeviceIoControl(handle, code, input, Span<byte>.Empty, out _, null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"DeviceIoControl 0x{code:X} on {path}");
        }
    }

    private static bool TryLink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
