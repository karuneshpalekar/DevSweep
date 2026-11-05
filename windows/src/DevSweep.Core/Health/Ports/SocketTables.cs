using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace DevSweep.Core.Health.Ports;

public enum PortProtocol
{
    Tcp,
    Udp,
}

/// <summary>One listening socket: protocol, local address and port, owning process.</summary>
public sealed record SocketEntry(PortProtocol Protocol, IPAddress Address, int Port, int Pid);

/// <summary>Reads listening sockets. Faked in tests.</summary>
public interface ISocketTable
{
    /// <summary>TCP listeners and UDP sockets, IPv4 and IPv6, with their owning process IDs.</summary>
    IReadOnlyList<SocketEntry> Listening();
}

public sealed class FixedSocketTable(params SocketEntry[] entries) : ISocketTable
{
    public IReadOnlyList<SocketEntry> Listening() => entries;
}

/// <summary>
/// <c>GetExtendedTcpTable</c> (<c>TCP_TABLE_OWNER_PID_LISTENER</c>) and <c>GetExtendedUdpTable</c>
/// (<c>UDP_TABLE_OWNER_PID</c>) for IPv4 and IPv6. The tables are parsed from their documented
/// layouts (<c>MIB_TCPROW_OWNER_PID</c>, <c>MIB_TCP6ROW_OWNER_PID</c>, <c>MIB_UDPROW_OWNER_PID</c>,
/// <c>MIB_UDP6ROW_OWNER_PID</c>), so the parser can be tested with built buffers.
/// </summary>
public sealed unsafe class WindowsSocketTable : ISocketTable
{
    public const int Tcp4Row = 24;
    public const int Tcp6Row = 56;
    public const int Udp4Row = 12;
    public const int Udp6Row = 28;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;

    public static WindowsSocketTable Instance { get; } = new();

    public IReadOnlyList<SocketEntry> Listening()
    {
        var list = new List<SocketEntry>();
        list.AddRange(ParseTcp4(Read(tcp: true, AfInet)));
        list.AddRange(ParseTcp6(Read(tcp: true, AfInet6)));
        list.AddRange(ParseUdp4(Read(tcp: false, AfInet)));
        list.AddRange(ParseUdp6(Read(tcp: false, AfInet6)));
        return list;
    }

    private static byte[] Read(bool tcp, uint family)
    {
        uint size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            byte[] buffer = new byte[Math.Max(size, 4)];
            uint result;
            fixed (byte* p = buffer)
            {
                result = tcp
                    ? PInvoke.GetExtendedTcpTable(p, &size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0)
                    : PInvoke.GetExtendedUdpTable(p, &size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
            }

            if (result == 0)
            {
                return buffer;
            }

            if (result != 122)
            {
                throw new System.ComponentModel.Win32Exception((int)result);
            }
        }

        throw new InvalidOperationException("The socket table kept growing while it was read.");
    }

    public static IReadOnlyList<SocketEntry> ParseTcp4(ReadOnlySpan<byte> table)
        => Parse(table, Tcp4Row, row => new SocketEntry(PortProtocol.Tcp, new IPAddress(row.Slice(4, 4)), Port(row[8..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(row[20..])));

    public static IReadOnlyList<SocketEntry> ParseTcp6(ReadOnlySpan<byte> table)
        => Parse(table, Tcp6Row, row => new SocketEntry(PortProtocol.Tcp, V6(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..])), Port(row[20..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(row[52..])));

    public static IReadOnlyList<SocketEntry> ParseUdp4(ReadOnlySpan<byte> table)
        => Parse(table, Udp4Row, row => new SocketEntry(PortProtocol.Udp, new IPAddress(row[..4]), Port(row[4..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(row[8..])));

    public static IReadOnlyList<SocketEntry> ParseUdp6(ReadOnlySpan<byte> table)
        => Parse(table, Udp6Row, row => new SocketEntry(PortProtocol.Udp, V6(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..])), Port(row[20..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(row[24..])));

    private delegate SocketEntry RowReader(ReadOnlySpan<byte> row);

    private static List<SocketEntry> Parse(ReadOnlySpan<byte> table, int rowSize, RowReader read)
    {
        var list = new List<SocketEntry>();
        if (table.Length < 4)
        {
            return list;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count && 4 + ((i + 1) * rowSize) <= table.Length; i++)
        {
            list.Add(read(table.Slice(4 + (i * rowSize), rowSize)));
        }

        return list;
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits of a DWORD.</summary>
    private static int Port(ReadOnlySpan<byte> dword) => (dword[0] << 8) | dword[1];

    private static IPAddress V6(ReadOnlySpan<byte> address, uint scope) => new(address, scope);

    /// <summary>Builds a table buffer (tests): count, then the given rows.</summary>
    public static byte[] Table(params byte[][] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        byte[] buffer = new byte[4 + rows.Sum(r => r.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        int offset = 4;
        foreach (byte[] row in rows)
        {
            row.CopyTo(buffer, offset);
            offset += row.Length;
        }

        return buffer;
    }

    /// <summary>An IPv4 TCP listener row (tests).</summary>
    public static byte[] Tcp4(IPAddress address, int port, int pid)
    {
        ArgumentNullException.ThrowIfNull(address);
        byte[] row = new byte[Tcp4Row];
        BinaryPrimitives.WriteUInt32LittleEndian(row, 2);
        address.GetAddressBytes().CopyTo(row, 4);
        row[8] = (byte)(port >> 8);
        row[9] = (byte)port;
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(20), (uint)pid);
        return row;
    }

    /// <summary>An IPv6 TCP listener row (tests).</summary>
    public static byte[] Tcp6(IPAddress address, int port, int pid)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException("An IPv6 address is needed.", nameof(address));
        }

        byte[] row = new byte[Tcp6Row];
        address.GetAddressBytes().CopyTo(row, 0);
        row[20] = (byte)(port >> 8);
        row[21] = (byte)port;
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(52), (uint)pid);
        return row;
    }
}
