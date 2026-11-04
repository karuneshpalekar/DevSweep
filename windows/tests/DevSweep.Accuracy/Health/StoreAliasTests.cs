using System.ComponentModel;
using System.Runtime.InteropServices;
using DevSweep.Core.Detectors;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Fixtures;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DevSweep.Accuracy.Health;

/// <summary>
/// Real <c>IO_REPARSE_TAG_APPEXECLINK</c> reparse points on NTFS, built the way the Store builds them:
/// the installer stub (<c>python.exe</c> that opens the Store) and a Store Python package's alias. DevSweep
/// must read them as aliases, never as files to measure or programs to run, report the stub as "not an
/// install" and the package as a Store Python.
/// </summary>
public sealed class StoreAliasTests(ITestOutputHelper output) : IDisposable
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const string StubFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    private const string StoreFamily = "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0";
    private readonly TempFixture _fixture = new("aliases");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void The_installer_stub_and_a_store_python_alias_are_told_apart_on_a_real_volume()
    {
        var environment = FixedEnvironment.UnderRoot(_fixture.Root);
        string apps = Path.Join(environment.KnownFolder("LocalAppData")!, "Microsoft", "WindowsApps");
        Directory.CreateDirectory(apps);
        string stub = Path.Join(apps, "python.exe");
        string store = Path.Join(apps, "python3.12.exe");
        MakeAlias(stub, StubFamily, StubFamily + "!PythonRedirector", @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.21.3482.0_x64__8wekyb3d8bbwe\AppInstallerPythonRedirector.exe");
        MakeAlias(store, StoreFamily, StoreFamily + "!Python", @"C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.12_3.12.2032.0_x64__qbz5n2kfra8p0\python3.12.exe");

        ProbeEntry entry = FileProbe.Stat(stub)!.Value;
        output.WriteLine($"stub attributes: {entry.Attributes}, length {entry.Length}");
        Assert.True(entry.IsReparsePoint);
        AliasInfo stubInfo = Assert.IsType<AliasInfo>(WindowsAliasReader.Instance.Read(stub));
        AliasInfo storeInfo = Assert.IsType<AliasInfo>(WindowsAliasReader.Instance.Read(store));
        output.WriteLine($"stub: {stubInfo}");
        output.WriteLine($"store: {storeInfo}");
        Assert.Equal(AliasKind.StoreInstallerStub, stubInfo.Kind);
        Assert.Equal(AliasKind.StorePython, storeInfo.Kind);
        Assert.Equal("3.12", storeInfo.PythonCycle);

        var registry = new InMemoryRegistry();
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, SearchPath.UserKey, ("Path", apps));
        var context = new ProbeContext(environment, registry, aliases: WindowsAliasReader.Instance, versions: WindowsFileVersionReader.Instance);
        var probe = new PythonProbe();
        List<Installation> installs = ProbeKit.Dedupe(probe.Find(context));
        CommandResolution resolution = RuntimeHealth.Resolve(context, "python", "python", []);
        RuntimeReport report = RuntimeReporter.Build(new ReportInput { Probe = probe, Installs = probe.MarkDefault(context, installs, resolution), Resolution = resolution, Today = DateOnly.FromDateTime(DateTime.UtcNow) });

        Installation only = Assert.Single(report.Installs);
        output.WriteLine($"install: {only.Version} {only.Source} {only.SourceDetail} [{only.VersionFrom}]");
        Assert.Equal(InstallSource.StorePython, only.Source);
        Assert.Equal(StoreFamily, only.SourceDetail);
        Assert.Equal(AliasKind.StoreInstallerStub, Assert.Single(resolution.Hits).Alias?.Kind);
        Assert.Contains(report.NotInstalls, n => n.Contains("not a Python install", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Text.Contains("opens the Microsoft Store", StringComparison.Ordinal));
        Assert.False(only.IsDefault);
    }

    private void MakeAlias(string path, string family, string appId, string target)
    {
        File.WriteAllBytes(path, []);
        byte[] data = AppExecLink.Build(family, appId, target);
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            Assert.Skip($"This account may not create app execution aliases here: {new Win32Exception(error).Message} ({Elevation.Describe()})");
        }

        _fixture.OnCleanup(() => File.Delete(path));
    }

#pragma warning disable SYSLIB1054 // A plain DllImport: this test project is not built with unsafe code, which LibraryImport needs.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
#pragma warning restore SYSLIB1054
}
