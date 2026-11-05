using System.Globalization;
using System.Runtime.InteropServices;
using DevSweep.Core.Health.Common;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>A program's version resource.</summary>
public sealed record FileVersion(string? ProductVersion, string? FixedVersion, string? ProductName, string? CompanyName)
{
    /// <summary>The product version string when it holds a dotted version, else the fixed file version.</summary>
    public string? Best => VersionText.FirstVersion(ProductVersion) ?? VersionText.FirstVersion(FixedVersion);
}

/// <summary>Reads version resources of programs without running them. Faked in tests.</summary>
public interface IFileVersionReader
{
    FileVersion? Read(string path);
}

/// <summary>Version resources in memory (tests).</summary>
public sealed class FixedFileVersions : IFileVersionReader
{
    public Dictionary<string, FileVersion> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FileVersion? Read(string path) => Versions.TryGetValue(path, out FileVersion? v) ? v : null;
}

/// <summary><c>GetFileVersionInfoW</c> / <c>VerQueryValueW</c> on a local file (never a link or cloud placeholder).</summary>
public sealed unsafe class WindowsFileVersionReader : IFileVersionReader
{
    public static WindowsFileVersionReader Instance { get; } = new();

    public FileVersion? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsWindows() || !HealthFiles.IsFile(path))
        {
            return null;
        }

        uint size = PInvoke.GetFileVersionInfoSize(path, out _);
        if (size == 0 || size > 4 * 1024 * 1024)
        {
            return null;
        }

        byte[] block = new byte[size];
        if (!PInvoke.GetFileVersionInfo(path, block))
        {
            return null;
        }

        fixed (byte* p = block)
        {
            string? fixedVersion = null;
            if (PInvoke.VerQueryValue(p, "\\", out void* info, out uint length) && length >= sizeof(VS_FIXEDFILEINFO))
            {
                var f = (VS_FIXEDFILEINFO*)info;
                fixedVersion = string.Create(CultureInfo.InvariantCulture,
                    $"{f->dwFileVersionMS >> 16}.{f->dwFileVersionMS & 0xFFFF}.{f->dwFileVersionLS >> 16}.{f->dwFileVersionLS & 0xFFFF}");
            }

            var languages = new List<string>();
            if (PInvoke.VerQueryValue(p, "\\VarFileInfo\\Translation", out void* translations, out uint tlen))
            {
                for (int i = 0; i + 4 <= tlen; i += 4)
                {
                    ushort lang = *(ushort*)((byte*)translations + i);
                    ushort codePage = *(ushort*)((byte*)translations + i + 2);
                    languages.Add(string.Create(CultureInfo.InvariantCulture, $"{lang:X4}{codePage:X4}"));
                }
            }

            languages.AddRange(["040904B0", "040904E4", "00000000"]);
            static string? Text(byte* p, List<string> languages, string name)
            {
                foreach (string language in languages)
                {
                    if (PInvoke.VerQueryValue(p, $"\\StringFileInfo\\{language}\\{name}", out void* value, out uint chars) && chars > 0)
                    {
                        string text = Marshal.PtrToStringUni((nint)value) ?? string.Empty;
                        if (text.Trim().Length > 0)
                        {
                            return text.Trim();
                        }
                    }
                }

                return null;
            }

            return new FileVersion(Text(p, languages, "ProductVersion") ?? Text(p, languages, "FileVersion"), fixedVersion, Text(p, languages, "ProductName"), Text(p, languages, "CompanyName"));
        }
    }
}
