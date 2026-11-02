using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace NufeCampus.Windows;

public sealed record NetworkConnection(string InterfaceId, string Name, string Kind);
public sealed record NetworkCounters(NetworkConnection Connection, long Sent, long Received, long Timestamp);
public sealed record NetworkSample(NetworkConnection? Connection, TrafficRate? Rate);

// Local interface counters only: no probes, authentication, or changes to routes/permissions.
public static class CurrentNetwork
{
    public static NetworkCounters? Read()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToArray();
        NetworkInterface? selected = null;
        foreach (var destination in new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111") })
        {
            var address = new IPEndPoint(destination, 0).Serialize();
            byte[] nativeAddress = new byte[address.Size];
            for (int i = 0; i < address.Size; i++) nativeAddress[i] = address[i];
            if (GetBestInterfaceEx(nativeAddress, out uint index) != 0) continue;
            selected = adapters.FirstOrDefault(n => InterfaceIndex(n, destination.AddressFamily) == index);
            if (selected != null) break;
        }
        // Also show a connected local network with no default route (e.g. before authentication).
        selected ??= adapters.OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ThenBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault(n => n.GetIPProperties().UnicastAddresses
                .Any(a => !IPAddress.IsLoopback(a.Address) && !a.Address.IsIPv6LinkLocal && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)));
        if (selected == null) return null;
        string kind = selected.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "以太网",
            NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "VPN / 隧道",
            _ => "网络接口"
        };
        string name = selected.Name;
        if (selected.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        {
            string? ssid = ReadSsid(selected.Id);
            if (ssid != null) name = ssid;
            else kind = "Wi-Fi · 网络名不可用";
        }
        var statistics = selected.GetIPStatistics();
        return new(new(selected.Id, name, kind), statistics.BytesSent, statistics.BytesReceived, Stopwatch.GetTimestamp());
    }

    private static int? InterfaceIndex(NetworkInterface adapter, AddressFamily family)
    {
        try { return family == AddressFamily.InterNetwork ? adapter.GetIPProperties().GetIPv4Properties()?.Index : adapter.GetIPProperties().GetIPv6Properties()?.Index; }
        catch (NetworkInformationException) { return null; }
    }

    private static string? ReadSsid(string interfaceId)
    {
        if (!Guid.TryParse(interfaceId, out var id) || Wifi.Native.Open(2, 0, out _, out var handle) != 0) return null;
        try
        {
            // wlan_intf_opcode_current_connection. An unavailable/denied SSID never blocks counters.
            if (Wifi.Native.Query(handle, ref id, 7, 0, out uint size, out var data, out _) != 0) return null;
            try
            {
                if (size < 556 || Marshal.ReadInt32(data) != 1) return null;
                int length = Marshal.ReadInt32(data + 520);
                if (length is < 1 or > 32) return null;
                byte[] bytes = new byte[length]; Marshal.Copy(data + 524, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { Wifi.Native.Free(data); }
        }
        finally { Wifi.Native.Close(handle, 0); }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx([In] byte[] destination, out uint index);
}
