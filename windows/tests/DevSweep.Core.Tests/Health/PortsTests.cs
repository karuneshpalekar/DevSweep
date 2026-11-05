using System.Net;
using DevSweep.Core.Health.Ports;
using Xunit;

namespace DevSweep.Core.Tests.Health;

public sealed class PortsTests
{
    [Fact]
    public void Socket_tables_are_parsed_from_their_documented_layouts()
    {
        byte[] v4 = WindowsSocketTable.Table(WindowsSocketTable.Tcp4(IPAddress.Any, 5173, 1200), WindowsSocketTable.Tcp4(IPAddress.Loopback, 5432, 880));
        byte[] v6 = WindowsSocketTable.Table(WindowsSocketTable.Tcp6(IPAddress.IPv6Loopback, 3000, 1300));

        IReadOnlyList<SocketEntry> tcp4 = WindowsSocketTable.ParseTcp4(v4);
        IReadOnlyList<SocketEntry> tcp6 = WindowsSocketTable.ParseTcp6(v6);

        Assert.Equal([(PortProtocol.Tcp, "0.0.0.0", 5173, 1200), (PortProtocol.Tcp, "127.0.0.1", 5432, 880)], tcp4.Select(e => (e.Protocol, e.Address.ToString(), e.Port, e.Pid)));
        Assert.Equal((IPAddress.IPv6Loopback, 3000, 1300), (tcp6[0].Address, tcp6[0].Port, tcp6[0].Pid));
        Assert.Empty(WindowsSocketTable.ParseTcp4([1, 0]));
        Assert.Single(WindowsSocketTable.ParseTcp4(v4.AsSpan(0, 4 + WindowsSocketTable.Tcp4Row + 3))); // a cut-off row is ignored
    }

    [Fact]
    public void Only_your_processes_are_listed_with_project_exposure_and_firewall()
    {
        string profile = Path.Join(Path.GetTempPath(), "devsweep-ports-unit-" + Guid.NewGuid().ToString("N")[..6], "Dev");
        string app = Path.Join(profile, "source", "shop");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Join(app, "package.json"), "{}");
        try
        {
            var sockets = new FixedSocketTable(
                new(PortProtocol.Tcp, IPAddress.Any, 5173, 100),
                new(PortProtocol.Tcp, IPAddress.IPv6Any, 5173, 100),
                new(PortProtocol.Tcp, IPAddress.Loopback, 8000, 200),
                new(PortProtocol.Tcp, IPAddress.Any, 445, 4),
                new(PortProtocol.Udp, IPAddress.Parse("192.168.1.20"), 5353, 300));
            var processes = new FixedProcessDetails();
            processes.Processes[100] = new ProcessInfo
            {
                Pid = 100, Name = "node", Path = @"C:\Program Files\nodejs\node.exe", OwnedByYou = true, WorkingDirectory = app,
                CommandLine = "node node_modules/vite/bin/vite.js --host --token=abc123",
            };
            processes.Processes[200] = new ProcessInfo
            {
                Pid = 200, Name = "python", Path = @"C:\Python312\python.exe", OwnedByYou = true, WorkingDirectory = profile,
                CommandLine = $"python \"{Path.Join(app, "manage.py")}\" runserver",
            };
            processes.Processes[4] = new ProcessInfo { Pid = 4, Name = "System", OwnedByYou = false };
            processes.Processes[300] = new ProcessInfo { Pid = 300, Name = "svchost", OwnedByYou = null };
            Firewall firewall = Firewall.Load(new FixedFirewallConfig
            {
                Rules = ["v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|Profile=Private|App=C:\\Program Files\\nodejs\\node.exe|Name=Node.js JavaScript Runtime|"],
            });

            PortsResult result = PortScanner.Scan(sockets, processes, firewall, profile);

            Assert.Equal(2, result.Ports.Count);
            Assert.Equal(2, result.OthersHidden);
            ListeningPort vite = result.Ports[0];
            Assert.Equal((5173, "Vite dev server in shop", app, FolderSource.WorkingDirectory, Exposure.AllInterfaces), (vite.Port, vite.Label, vite.Folder, vite.FolderSource, vite.Exposure));
            Assert.Equal(["0.0.0.0", "::"], vite.Addresses);
            Assert.Contains("--token=***", vite.CommandLine, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", vite.CommandLine, StringComparison.Ordinal);
            Assert.Equal("Allowed on Private networks; blocked on Domain and Public", vite.Firewall!.Summary);

            ListeningPort django = result.Ports[1];
            Assert.Equal(("Django dev server in shop", FolderSource.CommandLine, Exposure.Loopback), (django.Label, django.FolderSource, django.Exposure));
            Assert.Null(django.Firewall);

            PortsResult all = PortScanner.Scan(sockets, processes, firewall, profile, includeOthers: true);
            Assert.Equal(4, all.Ports.Count);
            Assert.Equal(("Windows (System)", false), (all.Ports.Single(p => p.Pid == 4).Label, all.Ports.Single(p => p.Pid == 4).IsDevelopment));
            Assert.Equal(Exposure.SpecificAddress, all.Ports.Single(p => p.Pid == 300).Exposure);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(profile)!, recursive: true);
        }
    }

    [Fact]
    public void Firewall_rules_block_before_allow_and_respect_profiles_ports_and_programs()
    {
        var config = new FixedFirewallConfig
        {
            Rules =
            [
                "v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=8000-8100|Name=Dev range|",
                "v2.31|Action=Block|Active=TRUE|Dir=In|Protocol=6|Profile=Public|LPort=8080|Name=No 8080 in public|",
                "v2.31|Action=Allow|Active=FALSE|Dir=In|Protocol=6|LPort=9000|Name=Disabled|",
                "v2.31|Action=Allow|Active=TRUE|Dir=Out|Protocol=6|LPort=9001|Name=Outbound|",
                "v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=17|LPort=9002|Name=UDP only|",
                "v2.31|Action=Allow|Active=TRUE|Dir=In|Svc=Dnscache|Name=Service only|",
                "v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=9003|RA4=LocalSubnet|Name=Subnet|",
                "not a rule",
            ],
        };
        config.Values["StandardProfile\\EnableFirewall"] = 0;
        Firewall firewall = Firewall.Load(config);

        FirewallVerdict v8080 = firewall.Judge(@"C:\x\app.exe", 8080, PortProtocol.Tcp);
        Assert.Equal([true, true, false], v8080.Profiles.Select(p => p.Allowed));
        Assert.Equal("the firewall is off for this profile", v8080.Profiles[1].Reason);
        Assert.Contains("No 8080 in public", v8080.Profiles[2].Reason, StringComparison.Ordinal);
        Assert.Equal([false, true, false], firewall.Judge(null, 9000, PortProtocol.Tcp).Profiles.Select(p => p.Allowed));
        Assert.Equal([false, true, false], firewall.Judge(null, 9001, PortProtocol.Tcp).Profiles.Select(p => p.Allowed));
        Assert.Equal([true, true, true], firewall.Judge(null, 9002, PortProtocol.Udp).Profiles.Select(p => p.Allowed));
        Assert.Contains("from some addresses", firewall.Judge(null, 9003, PortProtocol.Tcp).Profiles[0].Reason, StringComparison.Ordinal);

        Assert.False(Firewall.Load(new FixedFirewallConfig { Rules = null }).Judge(null, 1, PortProtocol.Tcp).Known);
        config.Values["DomainProfile\\DefaultInboundAction"] = 0;
        Assert.True(Firewall.Load(config).Judge(null, 1234, PortProtocol.Tcp).Profiles[0].Allowed);
    }

    [Theory]
    [InlineData("node server.js --password=hunter2", "node server.js --password=***")]
    [InlineData("app.exe --api-key \"x y\" run", "app.exe --api-key *** run")]
    [InlineData("psql -h localhost", "psql -h localhost")]
    public void Command_lines_never_show_secret_looking_values(string line, string shown) => Assert.Equal(shown, PortScanner.Redact(line));
}
