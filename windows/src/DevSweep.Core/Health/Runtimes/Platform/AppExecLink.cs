using System.Buffers.Binary;
using System.Text;
using DevSweep.Core.Platform;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>What an app execution alias in <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> stands for.</summary>
public enum AliasKind
{
    /// <summary>The App Installer stub that only opens the Store page (not a Python install).</summary>
    StoreInstallerStub,

    /// <summary>A Python package from the Microsoft Store (a real install).</summary>
    StorePython,

    /// <summary>Any other packaged app's alias.</summary>
    OtherPackage,
}

/// <summary>An <c>IO_REPARSE_TAG_APPEXECLINK</c> alias: the package it belongs to and the program it starts.</summary>
public sealed record AliasInfo(string PackageFamily, string AppUserModelId, string Target, AliasKind Kind)
{
    /// <summary>"3.12" for <c>PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0</c>.</summary>
    public string? PythonCycle
        => Kind == AliasKind.StorePython && VersionTextOf(PackageFamily) is { } v ? v : null;

    public string Describe() => Kind switch
    {
        AliasKind.StoreInstallerStub => "a Microsoft Store alias that only opens the Store page to install Python; it is not a Python install",
        AliasKind.StorePython => $"a Microsoft Store alias for {PackageFamily}",
        _ => $"an app alias for {PackageFamily}",
    };

    private static string? VersionTextOf(string family)
    {
        string name = family.Split('_')[0];
        string tail = name["PythonSoftwareFoundation.Python.".Length..];
        return Common.VersionText.FirstVersion(tail);
    }
}

/// <summary>Reads app execution aliases. Faked in tests.</summary>
public interface IAliasReader
{
    /// <summary>The alias at <paramref name="path"/>, or null when it is not one (or cannot be read).</summary>
    AliasInfo? Read(string path);
}

/// <summary>
/// Parses the reparse data of an app execution alias: tag <c>0x8000001B</c>, then a
/// version (3) and NUL-terminated UTF-16 strings: package family, app user model
/// id, target program (and, in version 3, the app type).
/// </summary>
public static class AppExecLink
{
    /// <summary>Header (tag, length, reserved) plus the version field.</summary>
    private const int HeaderBytes = 12;

    public static AliasInfo? Parse(ReadOnlySpan<byte> reparseBuffer)
    {
        if (reparseBuffer.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(reparseBuffer) != ReparseTags.AppExecLink)
        {
            return null;
        }

        int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(reparseBuffer[4..]);
        ReadOnlySpan<byte> data = reparseBuffer.Slice(8, Math.Min(dataLength, reparseBuffer.Length - 8));
        if (data.Length < 4)
        {
            return null;
        }

        var strings = new List<string>();
        ReadOnlySpan<byte> rest = data[4..];
        while (rest.Length >= 2 && strings.Count < 4)
        {
            int end = 0;
            while (end + 1 < rest.Length && (rest[end] != 0 || rest[end + 1] != 0))
            {
                end += 2;
            }

            strings.Add(Encoding.Unicode.GetString(rest[..end]));
            rest = end + 2 <= rest.Length ? rest[(end + 2)..] : [];
        }

        if (strings.Count < 3)
        {
            return null;
        }

        return new AliasInfo(strings[0], strings[1], strings[2], Classify(strings[0], strings[1], strings[2]));
    }

    public static AliasKind Classify(string packageFamily, string appUserModelId, string target)
    {
        ArgumentNullException.ThrowIfNull(packageFamily);
        ArgumentNullException.ThrowIfNull(appUserModelId);
        ArgumentNullException.ThrowIfNull(target);
        if (packageFamily.StartsWith("PythonSoftwareFoundation.Python.", StringComparison.OrdinalIgnoreCase))
        {
            return AliasKind.StorePython;
        }

        bool installer = packageFamily.StartsWith("Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase);
        bool redirector = appUserModelId.Contains("PythonRedirector", StringComparison.OrdinalIgnoreCase)
            || target.Contains("PythonRedirector", StringComparison.OrdinalIgnoreCase);
        return installer || redirector ? AliasKind.StoreInstallerStub : AliasKind.OtherPackage;
    }

    /// <summary>Builds the reparse data of an alias (tests and fixtures).</summary>
    public static byte[] Build(string packageFamily, string appUserModelId, string target, string appType = "0")
    {
        ArgumentNullException.ThrowIfNull(packageFamily);
        ArgumentNullException.ThrowIfNull(appUserModelId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(appType);
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(3u));
        foreach (string s in (string[])[packageFamily, appUserModelId, target, appType])
        {
            body.AddRange(Encoding.Unicode.GetBytes(s));
            body.Add(0);
            body.Add(0);
        }

        byte[] buffer = new byte[8 + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ReparseTags.AppExecLink);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)body.Count);
        body.CopyTo(buffer, 8);
        return buffer;
    }
}

/// <summary>Reads an alias's reparse data with <c>FSCTL_GET_REPARSE_POINT</c> (the alias is opened as itself, never run).</summary>
public sealed unsafe class WindowsAliasReader : IAliasReader
{
    public static WindowsAliasReader Instance { get; } = new();

    public AliasInfo? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using SafeFileHandle? handle = NativeFile.Open(LongPath.ToExtended(System.IO.Path.GetFullPath(path)), NativeFile.ReadAttributes, NativeFile.NoFollow, out _);
        if (handle is null)
        {
            return null;
        }

        byte[] buffer = new byte[16 * 1024];
        return PInvoke.DeviceIoControl(handle, PInvoke.FSCTL_GET_REPARSE_POINT, [], buffer, out uint returned, null)
            ? AppExecLink.Parse(buffer.AsSpan(0, (int)returned))
            : null;
    }
}
