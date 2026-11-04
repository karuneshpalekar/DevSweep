using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Health;

/// <summary>Tests that attach to other consoles (Ctrl+C) run on their own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleAttaching
{
    public const string Name = "Console attaching";
}

/// <summary>Ports on the real machine: our own listeners, compared with <c>netstat -ano</c>; process details; Windows Firewall; stopping processes.</summary>
[Collection(ConsoleAttaching.Name)]
public sealed class PortsAccuracyTests(ITestOutputHelper output)
{
    [Fact]
    public void Our_own_listeners_are_found_with_owner_command_line_and_working_directory()
    {
        using var loopback = new TcpListener(IPAddress.Loopback, 0);
        using var any = new TcpListener(IPAddress.Any, 0);
        loopback.Start();
        any.Start();
        int loopbackPort = ((IPEndPoint)loopback.LocalEndpoint).Port;
        int anyPort = ((IPEndPoint)any.LocalEndpoint).Port;

        Firewall firewall = Firewall.Load(WindowsFirewallConfig.Instance);
        output.WriteLine(firewall.Problem is null ? $"firewall: {firewall.RuleCount} rules read" : $"firewall: {firewall.Problem}");
        PortsResult result = PortScanner.Scan(WindowsSocketTable.Instance, WindowsProcessDetails.Instance, firewall, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (ListeningPort p in result.Ports)
        {
            output.WriteLine($"{p.Protocol} {p.Port} {p.Label} pid {p.Pid} [{string.Join(", ", p.Addresses)}] {p.Exposure} folder={p.Folder} ({p.FolderSource}) firewall={p.Firewall?.Summary}");
        }

        output.WriteLine($"{result.OthersHidden} listeners of other users hidden");
        ListeningPort mine = Assert.Single(result.Ports, p => p.Port == loopbackPort && p.Protocol == PortProtocol.Tcp);
        Assert.Equal(Environment.ProcessId, mine.Pid);
        Assert.Equal(Exposure.Loopback, mine.Exposure);
        Assert.True(mine.OwnedByYou);
        Assert.Null(mine.Firewall);
        ListeningPort open = Assert.Single(result.Ports, p => p.Port == anyPort && p.Protocol == PortProtocol.Tcp);
        Assert.Equal(Exposure.AllInterfaces, open.Exposure);
        Assert.NotNull(open.Firewall);
        output.WriteLine($"0.0.0.0:{anyPort}: {open.Firewall!.Summary}; {string.Join("; ", open.Firewall.Profiles.Select(v => $"{v.Profile}: {v.Reason}"))}");

        ProcessInfo self = WindowsProcessDetails.Instance.Read(Environment.ProcessId)!;
        output.WriteLine($"self: {self.Name} {self.Path} cwd={self.WorkingDirectory} owned={self.OwnedByYou} cmd={self.CommandLine}");
        Assert.True(self.OwnedByYou);
        Assert.Equal(Environment.ProcessPath, self.Path, ignoreCase: true);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), Path.TrimEndingDirectorySeparator(self.WorkingDirectory!), ignoreCase: true);
        Assert.Contains(Path.GetFileNameWithoutExtension(Environment.ProcessPath!), self.CommandLine!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tcp_listeners_match_netstat()
    {
        HashSet<(int Port, int Pid)> before = Ipv4Tcp();
        (int exit, string text) = Ntfs.Run("netstat.exe", "-ano -p TCP");
        HashSet<(int Port, int Pid)> after = Ipv4Tcp();
        Assert.Equal(0, exit);
        var netstat = new HashSet<(int, int)>();
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5 && parts[0] == "TCP" && parts[3] == "LISTENING" && !parts[1].StartsWith('[') && int.TryParse(parts[4], out int pid))
            {
                netstat.Add((int.Parse(parts[1][(parts[1].LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture), pid));
            }
        }

        List<(int, int)> missing = netstat.Where(e => !before.Contains(e) && !after.Contains(e)).ToList();
        List<(int, int)> extra = before.Intersect(after).Where(e => !netstat.Contains(e)).ToList();
        output.WriteLine($"netstat: {netstat.Count} IPv4 TCP listeners; DevSweep: {before.Count} / {after.Count}; missing {missing.Count}, extra {extra.Count}");
        Assert.Empty(missing);
        Assert.Empty(extra);
    }

    private static HashSet<(int, int)> Ipv4Tcp()
        => WindowsSocketTable.Instance.Listening().Where(e => e.Protocol == PortProtocol.Tcp && e.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(e => (e.Port, e.Pid)).ToHashSet();

    [Fact]
    public void A_console_program_stops_on_ctrl_c_and_one_that_ignores_it_is_ended_only_when_asked()
    {
        var stopper = new ProcessStopper(WindowsProcessDetails.Instance);
        using Process ping = StartInNewConsole("ping.exe", "-t 127.0.0.1");
        StopResult polite = stopper.AskToStop(ping.Id, TimeSpan.FromSeconds(10));
        output.WriteLine($"ping: {polite}");
        if (polite.Outcome == StopOutcome.CouldNotAsk)
        {
            ping.Kill();
            Assert.Skip($"No console could be attached here: {polite.Message}");
        }

        Assert.Equal(StopOutcome.Stopped, polite.Outcome);
        Assert.Equal("sent Ctrl+C to its console", polite.Method);
        Assert.True(ping.HasExited);

        string powershell = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using Process stubborn = StartInNewConsole(powershell, "-NoProfile -Command \"[Console]::TreatControlCAsInput = $true; Start-Sleep -Seconds 600\"");
        Thread.Sleep(2000);
        StopResult asked = stopper.AskToStop(stubborn.Id, TimeSpan.FromSeconds(3));
        output.WriteLine($"powershell ignoring Ctrl+C: {asked}");
        Assert.Equal(StopOutcome.StillRunning, asked.Outcome);
        Assert.False(stubborn.HasExited);
        StopResult ended = stopper.End(stubborn.Id);
        output.WriteLine($"after confirming: {ended}");
        Assert.Equal(StopOutcome.Stopped, ended.Outcome);
        Assert.True(stubborn.WaitForExit(5000));
    }

    [Fact]
    public void Windows_and_other_users_processes_are_never_stopped()
    {
        var stopper = new ProcessStopper(WindowsProcessDetails.Instance, _ => throw new InvalidOperationException("must not be asked"));
        Assert.Equal(StopOutcome.NotAllowed, stopper.AskToStop(4, TimeSpan.Zero).Outcome);
        Assert.Equal(StopOutcome.NotAllowed, stopper.AskToStop(Environment.ProcessId, TimeSpan.Zero).Outcome);
        Process? service = Process.GetProcessesByName("spoolsv").Concat(Process.GetProcessesByName("WmiPrvSE")).Concat(Process.GetProcessesByName("svchost")).FirstOrDefault();
        Assert.NotNull(service);
        StopResult refused = stopper.End(service.Id);
        output.WriteLine($"{service.ProcessName}: {refused}");
        Assert.True(refused.Outcome is StopOutcome.NotYours or StopOutcome.NotAllowed);
        Assert.False(service.HasExited);
    }

    private static Process StartInNewConsole(string program, string arguments)
    {
        var start = new ProcessStartInfo(program, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized };
        Process process = Process.Start(start)!;
        Thread.Sleep(1500);
        return process;
    }

    [Fact]
    public void The_firewall_configuration_is_read_or_reported_unknown()
    {
        Firewall firewall = Firewall.Load(WindowsFirewallConfig.Instance, text => DevSweep.Core.Health.Runtimes.SearchPath.Expand(text, Environment.GetEnvironmentVariable));
        output.WriteLine($"{(Elevation.IsAdministrator ? "administrator" : "standard user")}: {firewall.Problem ?? $"{firewall.RuleCount} rules"}");
        FirewallVerdict verdict = firewall.Judge(@"C:\does\not\exist.exe", 65000, PortProtocol.Tcp);
        output.WriteLine(verdict.Summary);
        Assert.True(verdict.Known ? verdict.Profiles.Count == 3 : verdict.Summary.StartsWith("Firewall unknown", StringComparison.Ordinal));
        if (firewall.Problem is null)
        {
            Assert.True(firewall.RuleCount > 10, "Windows ships with many inbound rules");
        }
    }
}
