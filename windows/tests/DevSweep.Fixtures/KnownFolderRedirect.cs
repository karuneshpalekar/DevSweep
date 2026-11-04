using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace DevSweep.Fixtures;

/// <summary>
/// Redirects a Known Folder the way "Move" in its Properties (or OneDrive
/// backup) does, with <c>SHSetKnownFolderPath</c>, and puts it back on dispose.
/// Nothing is moved; only where the shell says the folder is.
/// </summary>
public sealed unsafe class KnownFolderRedirect : IDisposable
{
    private readonly Guid _id;
    private readonly string _original;
    private bool _restored;

    private KnownFolderRedirect(Guid id, string original)
    {
        _id = id;
        _original = original;
    }

    public static KnownFolderRedirect Desktop(string newPath)
    {
        Guid id = PInvoke.FOLDERID_Desktop;
        PWSTR current;
        Check(PInvoke.SHGetKnownFolderPath(&id, KNOWN_FOLDER_FLAG.KF_FLAG_DONT_VERIFY, HANDLE.Null, &current), "SHGetKnownFolderPath");
        string original = current.ToString();
        Marshal.FreeCoTaskMem((nint)current.Value);
        var redirect = new KnownFolderRedirect(id, original);
        Set(id, newPath);
        return redirect;
    }

    public void Dispose()
    {
        if (!_restored)
        {
            Set(_id, _original);
            _restored = true;
        }
    }

    private static void Set(Guid id, string path)
    {
        fixed (char* p = path)
        {
            Check(PInvoke.SHSetKnownFolderPath(&id, 0, HANDLE.Null, p), $"SHSetKnownFolderPath {path}");
        }
    }

    private static void Check(HRESULT hr, string what)
    {
        if (hr.Failed)
        {
            throw new IOException($"{what} failed with 0x{hr.Value:X8}.", hr.Value);
        }
    }
}
