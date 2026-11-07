using DevSweep.App.ViewModels.Background;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DevSweep.App.Background;

/// <summary>
/// Toasts through the Windows App SDK's <see cref="AppNotificationManager"/>, which works for an unpackaged app:
/// <see cref="Register"/> gives DevSweep a notification identity (name and icon) and a COM activator, so a click
/// on a toast either reaches the running app (<c>NotificationInvoked</c>) or starts it with the toast's arguments.
/// </summary>
internal sealed partial class AppToasts : IToastNotifier, IDisposable
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private bool _registered;

    /// <summary>Raised on the UI thread when the user clicks a toast while the app is running.</summary>
    public event Action<ToastTarget>? Activated;

    public ToastAvailability Availability
    {
        get
        {
            if (!_registered)
            {
                return ToastAvailability.NotSupported;
            }

            try
            {
                return AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled ? ToastAvailability.Available : ToastAvailability.DisabledByUser;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return ToastAvailability.NotSupported;
            }
        }
    }

    /// <summary>The Windows App SDK payload (XML) a toast would carry; used by the smoke test to prove the builder works without showing anything.</summary>
    public static string BuildPayload(ToastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Build(message).Payload;
    }

    /// <summary>Registers for toast activation. Returns false (with the reason in the log) when this session cannot show toasts.</summary>
    public bool Register(string iconPath)
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Log.Write("Toasts: not supported in this session.");
                return false;
            }

            AppNotificationManager manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnInvoked;
            manager.Register("DevSweep", new Uri(iconPath));
            _registered = true;
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or FileNotFoundException)
        {
            Log.Write("Toasts: registration failed: " + ex.Message);
            return false;
        }
    }

    public bool Show(ToastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_registered)
        {
            return false;
        }

        try
        {
            AppNotification notification = Build(message);
            AppNotificationManager.Default.Show(notification);
            return notification.Id != 0;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Log.Write("Toasts: could not show a toast: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Closing anyway.
            }
        }
    }

    /// <summary>The target of a toast that started the app, or null when the app was not started by a toast.</summary>
    public static ToastTarget? LaunchTarget()
    {
        try
        {
            Microsoft.Windows.AppLifecycle.AppActivationArguments arguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            return arguments.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification && arguments.Data is AppNotificationActivatedEventArgs toast
                ? ToastMessage.ParseTarget(toast.Argument)
                : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AppNotification Build(ToastMessage message)
        => new AppNotificationBuilder()
            .AddArgument("target", message.Target.ToString())
            .AddText(message.Title)
            .AddText(message.Body)
            .BuildNotification();

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        ToastTarget target = ToastMessage.ParseTarget(args.Argument);
        _ui.TryEnqueue(() => Activated?.Invoke(target));
    }
}
