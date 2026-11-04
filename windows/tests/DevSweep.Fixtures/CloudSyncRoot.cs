using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Fixtures;

/// <summary>What the Cloud Filter says about one placeholder (CfGetPlaceholderInfo, standard class).</summary>
/// <param name="OnDiskDataSize">Bytes of file data present on this disk; 0 means fully dehydrated.</param>
/// <param name="PinState">Pinned, unpinned, unspecified...</param>
/// <param name="InSync">True when in sync.</param>
public readonly record struct PlaceholderInfo(long OnDiskDataSize, string PinState, bool InSync);

/// <summary>
/// A real Cloud Files sync root with a test provider (PLAN 6: "fake OneDrive
/// placeholders via the Cloud Filter API"). Registers a folder with
/// <c>CfRegisterSyncRoot</c>, connects with <c>CfConnectSyncRoot</c> and a
/// callback table that only counts requests (any FETCH_DATA call means something
/// tried to hydrate a file), and creates dehydrated placeholders with
/// <c>CfCreatePlaceholders</c>.
/// </summary>
public sealed unsafe class CloudSyncRoot : IDisposable
{
    private static int s_fetchData;
    private static int s_fetchPlaceholders;
    private readonly int _fetchDataAtStart;
    private readonly int _fetchPlaceholdersAtStart;
    private CF_CONNECTION_KEY _connection;
    private bool _connected;
    private bool _registered;

    private CloudSyncRoot(string root)
    {
        Root = root;
        _fetchDataAtStart = Volatile.Read(ref s_fetchData);
        _fetchPlaceholdersAtStart = Volatile.Read(ref s_fetchPlaceholders);
    }

    public string Root { get; }

    /// <summary>FETCH_DATA callbacks since this root was created: each is an attempt to hydrate a placeholder.</summary>
    public int FetchDataRequests => Volatile.Read(ref s_fetchData) - _fetchDataAtStart;

    /// <summary>FETCH_PLACEHOLDERS callbacks: each is an attempt to populate a folder from the provider.</summary>
    public int FetchPlaceholderRequests => Volatile.Read(ref s_fetchPlaceholders) - _fetchPlaceholdersAtStart;

    /// <summary>
    /// Registers and connects <paramref name="root"/> (an empty folder on NTFS).
    /// Null with a precise reason when this machine cannot host a sync root.
    /// </summary>
    public static CloudSyncRoot? TryCreate(string root, out string skipReason)
    {
        Directory.CreateDirectory(root);
        var syncRoot = new CloudSyncRoot(root);
        HRESULT hr = syncRoot.Register();
        if (hr.Failed && Elevation.IsAdministrator)
        {
            // The Cloud Files mini-filter may not be loaded on Windows Server; an administrator can load it.
            Ntfs.Run("fltmc.exe", "load CldFlt");
            hr = syncRoot.Register();
        }

        if (hr.Failed)
        {
            skipReason = $"CfRegisterSyncRoot failed with 0x{hr.Value:X8} ({Marshal.GetExceptionForHR(hr.Value)?.Message}); the Cloud Files filter (cldflt.sys) is not available to this account on this machine.";
            return null;
        }

        syncRoot._registered = true;
        hr = syncRoot.Connect();
        if (hr.Failed)
        {
            syncRoot.Dispose();
            skipReason = $"CfConnectSyncRoot failed with 0x{hr.Value:X8} ({Marshal.GetExceptionForHR(hr.Value)?.Message}).";
            return null;
        }

        syncRoot._connected = true;
        skipReason = string.Empty;
        return syncRoot;
    }

    /// <summary>Creates dehydrated file placeholders (in sync, no data on disk) in <paramref name="relativeFolder"/>.</summary>
    public void CreateFilePlaceholders(string relativeFolder, IEnumerable<(string Name, long Size)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach ((string name, long size) in files)
        {
            Create(relativeFolder, name, size, directory: false, populated: false);
        }
    }

    /// <summary>
    /// Creates a folder placeholder. <paramref name="populated"/>: marked as fully
    /// populated (its listing never asks the provider); otherwise populated on demand.
    /// </summary>
    public void CreateFolderPlaceholder(string relativeFolder, string name, bool populated)
        => Create(relativeFolder, name, 0, directory: true, populated);

    /// <summary>Turns an ordinary file inside the root into an in-sync placeholder whose data stays on disk ("available on this device").</summary>
    public static void ConvertToPlaceholder(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] identity = Encoding.UTF8.GetBytes(Path.GetFileName(path));
        Check(PInvoke.CfConvertToPlaceholder(handle, identity, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC), $"CfConvertToPlaceholder {path}");
    }

    /// <summary>Reads a placeholder's state through a metadata-only, no-recall handle (reading it does not hydrate it).</summary>
    public static PlaceholderInfo GetInfo(string path)
    {
        using SafeFileHandle handle = PInvoke.CreateFile(
            path,
            (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_NO_RECALL | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
            null);
        if (handle.IsInvalid)
        {
            throw new IOException($"Cannot open {path} (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        Span<byte> buffer = stackalloc byte[1024];
        Check(PInvoke.CfGetPlaceholderInfo(handle, CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD, buffer, out _), $"CfGetPlaceholderInfo {path}");
        CF_PLACEHOLDER_STANDARD_INFO info = MemoryMarshal.Read<CF_PLACEHOLDER_STANDARD_INFO>(buffer);
        return new PlaceholderInfo(info.OnDiskDataSize, info.PinState.ToString(), info.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);
    }

    public void Dispose()
    {
        if (_connected)
        {
            PInvoke.CfDisconnectSyncRoot(_connection);
            _connected = false;
        }

        if (_registered)
        {
            PInvoke.CfUnregisterSyncRoot(Root);
            _registered = false;
        }
    }

    private static void Check(HRESULT hr, string what)
    {
        if (hr.Failed)
        {
            throw new IOException($"{what} failed with 0x{hr.Value:X8}.", hr.Value);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
        => Interlocked.Increment(ref s_fetchData);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
        => Interlocked.Increment(ref s_fetchPlaceholders);

    private HRESULT Register()
    {
        byte[] identity = Encoding.UTF8.GetBytes("DevSweep accuracy suite");
        fixed (char* name = "DevSweepTestProvider")
        fixed (char* version = "1.0")
        fixed (byte* id = identity)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = name,
                ProviderVersion = version,
                SyncRootIdentity = id,
                SyncRootIdentityLength = (uint)identity.Length,
                ProviderId = new Guid("5b0e2a3c-6f0d-4d8e-9a3b-0d5e7c1f2a90"),
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
                PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_DEFAULT,
            };
            policies.Hydration.Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL;
            policies.Population.Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL;
            return PInvoke.CfRegisterSyncRoot(Root, in registration, in policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT);
        }
    }

    private HRESULT Connect()
    {
        CF_CALLBACK_REGISTRATION[] table =
        [
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = &OnFetchData },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = &OnFetchPlaceholders },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
        ];
        return PInvoke.CfConnectSyncRoot(Root, table, null, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO, out _connection);
    }

    private void Create(string relativeFolder, string name, long size, bool directory, bool populated)
    {
        string baseFolder = Path.Combine(Root, relativeFolder);
        Directory.CreateDirectory(baseFolder);
        byte[] identity = RandomNumberGenerator.GetBytes(16);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        CF_PLACEHOLDER_CREATE_FLAGS flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;
        if (directory && populated)
        {
            flags |= CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION;
        }

        fixed (char* fileName = name)
        fixed (byte* id = identity)
        {
            var info = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = fileName,
                FileIdentity = id,
                FileIdentityLength = (uint)identity.Length,
                Flags = flags,
            };
            info.FsMetadata.FileSize = size;
            info.FsMetadata.BasicInfo.FileAttributes = directory ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal;
            info.FsMetadata.BasicInfo.CreationTime = now;
            info.FsMetadata.BasicInfo.LastWriteTime = now;
            info.FsMetadata.BasicInfo.LastAccessTime = now;
            info.FsMetadata.BasicInfo.ChangeTime = now;
            uint processed;
            HRESULT hr;
            fixed (char* folder = baseFolder)
            {
                hr = PInvoke.CfCreatePlaceholders(folder, &info, 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed);
            }

            Check(hr.Failed ? hr : info.Result, $"CfCreatePlaceholders {name}");
        }
    }
}
