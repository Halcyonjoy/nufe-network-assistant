using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
namespace NufeCampus.Windows;

public sealed record WifiState(string Kind, string? Address = null, string? Mac = null);
public static class Wifi
{
    public static WifiState Read()
    {
        uint opened = Native.Open(2, 0, out _, out var handle);
        if (opened != 0) return new(opened == 5 ? "permission" : "off_campus");
        try
        {
            uint enumerated = Native.Enum(handle, 0, out var list);
            if (enumerated != 0) return new(enumerated == 5 ? "permission" : "off_campus");
            try
            {
                int count = Marshal.ReadInt32(list);
                if (count is < 0 or > 64) return new("off_campus");
                for (int i = 0; i < count; i++)
                {
                    nint item = list + 8 + i * 532;
                    if (Marshal.ReadInt32(item + 528) != 1) continue;
                    var id = Marshal.PtrToStructure<Guid>(item);
                    uint queried = Native.Query(handle, ref id, 7, 0, out var size, out var data, out _);
                    if (queried == 5) return new("permission");
                    if (queried != 0) continue;
                    try
                    {
                        if (size < 560 || Marshal.ReadInt32(data + 520) != 8) continue;
                        var bytes = new byte[8]; Marshal.Copy(data + 524, bytes, 0, 8);
                        if (Encoding.ASCII.GetString(bytes) != "NUFE-STU") continue;
                        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id.Trim('{', '}').Equals(id.ToString(), StringComparison.OrdinalIgnoreCase));
                        var ipv4 = adapter?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                        return new("nufe", ipv4?.Address.ToString(), adapter?.GetPhysicalAddress().ToString());
                    }
                    finally { Native.Free(data); }
                }
            }
            finally { Native.Free(list); }
        }
        finally { Native.Close(handle, 0); }
        return new("off_campus");
    }
    public static string NormalizeMac(string value) => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    internal static class Native
    {
        [DllImport("wlanapi.dll", EntryPoint = "WlanOpenHandle")] internal static extern uint Open(uint version, nint reserved, out uint negotiated, out nint handle);
        [DllImport("wlanapi.dll", EntryPoint = "WlanEnumInterfaces")] internal static extern uint Enum(nint handle, nint reserved, out nint list);
        [DllImport("wlanapi.dll", EntryPoint = "WlanQueryInterface")] internal static extern uint Query(nint handle, ref Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint type);
        [DllImport("wlanapi.dll", EntryPoint = "WlanFreeMemory")] internal static extern void Free(nint data);
        [DllImport("wlanapi.dll", EntryPoint = "WlanCloseHandle")] internal static extern uint Close(nint handle, nint reserved);
        internal delegate void Callback(nint data, nint context);
        [DllImport("wlanapi.dll", EntryPoint = "WlanRegisterNotification")] internal static extern uint Register(nint handle, uint sources, [MarshalAs(UnmanagedType.Bool)] bool ignore, Callback? callback, nint context, nint reserved, out uint previous);
    }
}
public sealed class WifiEvents : IDisposable
{
    private nint handle;
    private readonly Wifi.Native.Callback callback;
    private readonly NetworkAddressChangedEventHandler changed;
    public WifiEvents(Action pulse)
    {
        callback = (_, _) => pulse(); changed = (_, _) => pulse();
        NetworkChange.NetworkAddressChanged += changed;
        if (Wifi.Native.Open(2, 0, out _, out handle) == 0) Wifi.Native.Register(handle, 8, true, callback, 0, 0, out _);
    }
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= changed;
        if (handle != 0) { Wifi.Native.Register(handle, 0, true, null, 0, 0, out _); Wifi.Native.Close(handle, 0); handle = 0; }
        GC.KeepAlive(callback);
    }
}
