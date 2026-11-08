namespace DevSweep.App.Background;

/// <summary>
/// One DevSweep per user session: a second start (the Start menu while it sits in the tray, say) tells the first to
/// show its window and exits, so there is one tray icon, one scheduler and one update check.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DevSweep.App.Instance";
    private const string ShowEventName = @"Local\DevSweep.App.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show, bool isFirst)
    {
        _mutex = mutex;
        _show = show;
        IsFirst = isFirst;
    }

    /// <summary>True when no other DevSweep is running for this user.</summary>
    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var show = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName);
        return new SingleInstance(mutex, show, createdNew);
    }

    /// <summary>Asks the running copy to show its window.</summary>
    public void SignalFirst() => _show.Set();

    /// <summary>Calls <paramref name="onShow"/> (on a thread-pool thread) whenever another start asks the window to show.</summary>
    public void ListenForSecondStarts(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (IsFirst)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _show.Dispose();
    }
}
