using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace DevSweep.App.Toolkit;

/// <summary>Explorer and the clipboard. Nothing here changes files.</summary>
internal static class Shell
{
    /// <summary>Opens File Explorer with the item selected (or its folder when it is gone).</summary>
    public static void Reveal(string path)
    {
        try
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var start = new ProcessStartInfo(Path.Join(windows, "explorer.exe")) { UseShellExecute = false };
            if (File.Exists(path) || Directory.Exists(path))
            {
                start.ArgumentList.Add("/select," + path);
            }
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
            {
                start.ArgumentList.Add(parent);
            }
            else
            {
                return;
            }

            using Process? process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer could not be started; nothing else to do.
        }
    }

    public static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
