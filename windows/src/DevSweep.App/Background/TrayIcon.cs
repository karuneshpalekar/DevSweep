using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DevSweep.App.ViewModels.Background;

namespace DevSweep.App.Background;

/// <summary>
/// The notification-area icon: a hidden top-level window that receives the icon's callbacks, an icon drawn in
/// code, and a native pop-up menu built from <see cref="TrayViewModel.Items"/> each time it opens, so free
/// space, alerts and recent projects are always current. Click opens DevSweep; right-click (or the menu key)
/// opens the menu. Nothing in the menu cleans, moves or deletes anything.
/// </summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const string ClassName = "DevSweep.TrayWindow";
    private const int MaxMenuText = 90;

    // All of this runs on the UI thread (the window is created on it and its messages are dispatched by its message loop).
    private static readonly Dictionary<nint, TrayIcon> Instances = [];
    private static bool _classRegistered;

    private readonly TrayViewModel _model;
    private readonly uint _taskbarCreated;
    private nint _window;
    private nint _icon;
    private bool _disposed;

    public TrayIcon(TrayViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
        RegisterClass();
        fixed (char* name = ClassName)
        {
            _window = TrayNative.CreateWindow(0, name, name, 0, 0, 0, 0, 0, 0, 0, TrayNative.GetModuleHandle(null), 0);
        }

        if (_window == 0)
        {
            throw new InvalidOperationException("The tray icon's window could not be created (error " + Marshal.GetLastPInvokeError() + ").");
        }

        Instances[_window] = this;
        _icon = CreateIcon();
        if (_icon == 0)
        {
            throw new InvalidOperationException("The tray icon could not be drawn (error " + Marshal.GetLastPInvokeError() + ").");
        }

        Added = AddIcon();
    }

    /// <summary>True when the shell accepted the icon (false when there is no taskbar, as in some service sessions).</summary>
    public bool Added { get; private set; }

    /// <summary>Refreshes the hover text (free space, how many things need attention).</summary>
    public void UpdateTooltip()
    {
        if (_disposed || !Added)
        {
            return;
        }

        TrayNative.NotifyIconData data = Data(TrayNative.NifTip, _model.Tooltip());
        TrayNative.ShellNotifyIcon(TrayNative.NimModify, &data);
    }

    /// <summary>Builds and destroys the menu without showing it: proves the menu code works (the smoke test). Returns the item count.</summary>
    public int BuildAndDiscardMenu()
    {
        nint menu = BuildMenu(_model.Items(), out int count);
        TrayNative.DestroyMenu(menu);
        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Added)
        {
            TrayNative.NotifyIconData data = Data(0, null);
            TrayNative.ShellNotifyIcon(TrayNative.NimDelete, &data);
            Added = false;
        }

        Instances.Remove(_window);
        if (_window != 0)
        {
            TrayNative.DestroyWindow(_window);
            _window = 0;
        }

        if (_icon != 0)
        {
            TrayNative.DestroyIcon(_icon);
            _icon = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(window, out TrayIcon? icon))
        {
#pragma warning disable CA1031 // A callback from native code must never let an exception escape.
            try
            {
                icon.OnMessage(message, wParam, lParam);
            }
            catch (Exception ex)
            {
                Log.Write("Tray: " + ex.Message);
            }
#pragma warning restore CA1031
        }

        return TrayNative.DefWindowProc(window, message, wParam, lParam);
    }

    private static void RegisterClass()
    {
        if (_classRegistered)
        {
            return;
        }

        fixed (char* name = ClassName)
        {
            var windowClass = new TrayNative.WindowClass
            {
                Size = (uint)sizeof(TrayNative.WindowClass),
                WindowProc = &WindowProc,
                Instance = TrayNative.GetModuleHandle(null),
                ClassName = name,
            };
            if (TrayNative.RegisterClass(&windowClass) == 0)
            {
                throw new InvalidOperationException("The tray icon's window class could not be registered (error " + Marshal.GetLastPInvokeError() + ").");
            }
        }

        _classRegistered = true;
    }

    private void OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == TrayNative.WmApp + 1)
        {
            switch ((uint)lParam & 0xFFFF)
            {
                case TrayNative.NinSelect or TrayNative.NinKeySelect or TrayNative.WmLButtonUp:
                    _model.Execute(new TrayItem("Open DevSweep", TrayCommand.OpenWindow));
                    break;
                case TrayNative.WmContextMenu or TrayNative.WmRButtonUp:
                    ShowMenu();
                    break;
                default:
                    break;
            }
        }
        else if (message == _taskbarCreated)
        {
            // Explorer restarted: the icon is gone and must be added again.
            Added = AddIcon();
        }
    }

    private void ShowMenu()
    {
        IReadOnlyList<TrayItem> items = _model.Items();
        nint menu = BuildMenu(items, out _);
        try
        {
            TrayNative.Point point = default;
            TrayNative.GetCursorPos(&point);
            // The window must be foreground for the menu to close when the user clicks elsewhere.
            TrayNative.SetForegroundWindow(_window);
            int chosen = TrayNative.TrackPopupMenu(menu, TrayNative.TpmReturnCmd | TrayNative.TpmRightButton | TrayNative.TpmBottomAlign, point.X, point.Y, 0, _window, 0);
            TrayNative.PostMessage(_window, TrayNative.WmNull, 0, 0);
            if (chosen is > 0 && chosen <= items.Count)
            {
                _model.Execute(items[chosen - 1]);
            }
        }
        finally
        {
            TrayNative.DestroyMenu(menu);
        }
    }

    private static nint BuildMenu(IReadOnlyList<TrayItem> items, out int count)
    {
        nint menu = TrayNative.CreatePopupMenu();
        if (menu == 0)
        {
            throw new InvalidOperationException("The tray menu could not be created (error " + Marshal.GetLastPInvokeError() + ").");
        }

        count = 0;
        for (int i = 0; i < items.Count; i++)
        {
            TrayItem item = items[i];
            if (item.IsSeparator)
            {
                TrayNative.AppendMenu(menu, TrayNative.MfSeparator, 0, null);
                continue;
            }

            string text = item.Text.Length > MaxMenuText ? item.Text[..(MaxMenuText - 1)] + "…" : item.Text;
            // '&' marks a keyboard accelerator in a menu; a title with one must show it literally.
            TrayNative.AppendMenu(menu, TrayNative.MfString | (item.Enabled ? 0 : TrayNative.MfGrayed), (nuint)(i + 1), text.Replace("&", "&&", StringComparison.Ordinal));
            count++;
        }

        return menu;
    }

    private bool AddIcon()
    {
        TrayNative.NotifyIconData data = Data(TrayNative.NifMessage | TrayNative.NifIcon | TrayNative.NifTip, _model.Tooltip());
        data.CallbackMessage = TrayNative.WmApp + 1;
        data.Icon = _icon;
        if (!TrayNative.ShellNotifyIcon(TrayNative.NimAdd, &data))
        {
            return false;
        }

        data.Version = 4;
        TrayNative.ShellNotifyIcon(TrayNative.NimSetVersion, &data);
        return true;
    }

    private TrayNative.NotifyIconData Data(uint flags, string? tip)
    {
        var data = new TrayNative.NotifyIconData
        {
            Size = (uint)sizeof(TrayNative.NotifyIconData),
            Window = _window,
            Id = 1,
            Flags = flags,
        };
        if (tip is not null)
        {
            int length = Math.Min(tip.Length, 127);
            for (int i = 0; i < length; i++)
            {
                data.Tip[i] = tip[i];
            }

            data.Tip[length] = '\0';
        }

        return data;
    }

    private static nint CreateIcon()
    {
        int size = Math.Max(16, TrayNative.GetSystemMetrics(TrayNative.SmCxSmIcon));
        byte[] color = AppIconImage.Bgra(size);
        byte[] mask = new byte[((size + 15) / 16 * 2) * size];
        nint colorBitmap;
        nint maskBitmap;
        fixed (byte* c = color)
        fixed (byte* m = mask)
        {
            colorBitmap = TrayNative.CreateBitmap(size, size, 1, 32, c);
            maskBitmap = TrayNative.CreateBitmap(size, size, 1, 1, m);
        }

        try
        {
            if (colorBitmap == 0 || maskBitmap == 0)
            {
                return 0;
            }

            var info = new TrayNative.IconInfo { IsIcon = 1, Mask = maskBitmap, Color = colorBitmap };
            return TrayNative.CreateIconIndirect(&info);
        }
        finally
        {
            if (colorBitmap != 0)
            {
                TrayNative.DeleteObject(colorBitmap);
            }

            if (maskBitmap != 0)
            {
                TrayNative.DeleteObject(maskBitmap);
            }
        }
    }
}
