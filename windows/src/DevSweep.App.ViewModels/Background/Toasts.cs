using System.Globalization;
using DevSweep.Core.Health.Alerts;

namespace DevSweep.App.ViewModels.Background;

/// <summary>Which screen a notification opens when it is clicked.</summary>
public enum ToastTarget
{
    Home,
    CleanUp,
    HealthTools,
    HealthSecurity,
}

/// <summary>What a toast says and where a click on it goes.</summary>
public sealed record ToastMessage(string Title, string Body, ToastTarget Target)
{
    private const string Key = "target";

    /// <summary>The launch argument stored in the toast (<c>target=CleanUp</c>).</summary>
    public string Argument => Key + "=" + Target;

    /// <summary>Reads the target back from a toast's arguments; anything unknown opens Home.</summary>
    public static ToastTarget ParseTarget(IReadOnlyDictionary<string, string>? arguments)
        => arguments is not null && arguments.TryGetValue(Key, out string? value) && Enum.TryParse(value, ignoreCase: false, out ToastTarget target) && Enum.IsDefined(target)
            ? target
            : ToastTarget.Home;

    /// <summary>Reads the target back from a launch string such as <c>target=HealthTools</c>.</summary>
    public static ToastTarget ParseTarget(string? argument)
    {
        if (argument is null)
        {
            return ToastTarget.Home;
        }

        foreach (string pair in argument.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && pair[..eq] == Key && Enum.TryParse(pair[(eq + 1)..], ignoreCase: false, out ToastTarget target) && Enum.IsDefined(target))
            {
                return target;
            }
        }

        return ToastTarget.Home;
    }
}

public enum ToastAvailability
{
    Available,

    /// <summary>The user turned notifications off for DevSweep (or Focus assist / policy does).</summary>
    DisabledByUser,

    NotSupported,
}

/// <summary>Shows notifications (Windows App SDK's AppNotificationManager in the app; a recorder in tests).</summary>
public interface IToastNotifier
{
    ToastAvailability Availability { get; }

    /// <summary>Shows the toast; false when it could not be shown.</summary>
    bool Show(ToastMessage message);
}

/// <summary>No notifications at all (screenshots, smoke runs, tests that do not look at toasts).</summary>
public sealed class NoToasts : IToastNotifier
{
    public ToastAvailability Availability => ToastAvailability.NotSupported;

    public bool Show(ToastMessage message) => false;
}

/// <summary>Turns what the alert rules found into toasts: several problems become one.</summary>
public static class AlertToasts
{
    /// <summary>At most this many problems are named in a combined toast; the rest are counted.</summary>
    public const int MaxListed = 3;

    public static ToastTarget TargetOf(AlertTarget target) => target switch
    {
        AlertTarget.CleanUp => ToastTarget.CleanUp,
        AlertTarget.HealthTools => ToastTarget.HealthTools,
        AlertTarget.HealthSecurity => ToastTarget.HealthSecurity,
        _ => ToastTarget.Home,
    };

    /// <summary>
    /// One problem keeps its own title, text and screen. Several become one toast listing them; a click
    /// opens their screen when they share one, else Home (whose "Needs attention" lists them all).
    /// </summary>
    public static ToastMessage? Compose(IReadOnlyList<AlertEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return null;
        }

        if (events.Count == 1)
        {
            AlertEvent only = events[0];
            return new ToastMessage(only.Title, only.Body, TargetOf(only.Target));
        }

        var targets = events.Select(e => TargetOf(e.Target)).Distinct().ToList();
        string title = string.Create(CultureInfo.InvariantCulture, $"DevSweep found {events.Count} things to look at");
        List<string> lines = [.. events.Take(MaxListed).Select(e => e.Title)];
        if (events.Count > MaxListed)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"and {events.Count - MaxListed} more"));
        }

        return new ToastMessage(title, string.Join("\n", lines), targets.Count == 1 ? targets[0] : ToastTarget.Home);
    }
}
