using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DevSweep.App.ViewModels.Background;

public enum LoginState
{
    Off,
    On,

    /// <summary>DevSweep's entry exists but Windows (Settings, Apps, Startup, or Task Manager) turned it off.</summary>
    DisabledInWindows,
}

/// <summary>"Open at login": DevSweep starts in the tray when the user signs in. Never needs administrator rights.</summary>
public interface ILoginItem
{
    LoginState State { get; }

    /// <summary>Turns it on or off. Throws <see cref="InvalidOperationException"/> with a readable message when Windows refuses.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>The per-user Run key and Windows' own on/off record for it, behind an interface so the logic is testable.</summary>
public interface IRunKeyStore
{
    string? GetRun(string name);

    void SetRun(string name, string command);

    void DeleteRun(string name);

    /// <summary>Windows' record for the entry (<c>StartupApproved\Run</c>): first byte odd means turned off.</summary>
    byte[]? GetApproved(string name);

    void DeleteApproved(string name);
}

/// <summary>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>: the current user's own key, so no administrator
/// rights, UAC prompt or machine-wide change is ever involved. Off by default; the entry is written only
/// when the user turns the switch on and removed when they turn it off.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryRunKeyStore(string runPath = RegistryRunKeyStore.DefaultRunPath, string approvedPath = RegistryRunKeyStore.DefaultApprovedPath) : IRunKeyStore
{
    public const string DefaultRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? GetRun(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetRun(string name, string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(runPath, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void DeleteRun(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public byte[]? GetApproved(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(approvedPath, writable: false);
        return key?.GetValue(name) as byte[];
    }

    public void DeleteApproved(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(approvedPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>The Run-key login item for one executable.</summary>
public sealed class RunKeyLoginItem : ILoginItem
{
    public const string ValueName = "DevSweep";

    /// <summary>The argument the login entry passes: start hidden in the tray.</summary>
    public const string BackgroundArgument = "--background";

    private readonly IRunKeyStore _store;

    public RunKeyLoginItem(IRunKeyStore store, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _store = store;
        Command = Quote(executablePath) + " " + BackgroundArgument;
    }

    /// <summary>The command line Windows runs at sign-in.</summary>
    public string Command { get; }

    public LoginState State
    {
        get
        {
            try
            {
                if (_store.GetRun(ValueName) is null)
                {
                    return LoginState.Off;
                }

                return _store.GetApproved(ValueName) is { Length: > 0 } approved && (approved[0] & 1) == 1
                    ? LoginState.DisabledInWindows
                    : LoginState.On;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return LoginState.Off;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                _store.SetRun(ValueName, Command);
                _store.DeleteApproved(ValueName);
            }
            else
            {
                _store.DeleteRun(ValueName);
                _store.DeleteApproved(ValueName);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("Windows would not let DevSweep change its start-up entry: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Rewrites the entry when it is on but names another copy of DevSweep (the folder was moved or updated), so
    /// the login start keeps working. Never turns it on.
    /// </summary>
    public bool RepairIfMoved()
    {
        try
        {
            if (_store.GetRun(ValueName) is { } current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                _store.SetRun(ValueName, Command);
                return true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Leave it as it is; Settings shows the state.
        }

        return false;
    }

    private static string Quote(string path) => "\"" + path + "\"";
}

/// <summary>Keeps the state in memory (screenshots, tests, runs with no Windows integration).</summary>
public sealed class MemoryLoginItem(LoginState state = LoginState.Off) : ILoginItem
{
    public LoginState State { get; private set; } = state;

    public void SetEnabled(bool enabled) => State = enabled ? LoginState.On : LoginState.Off;
}
