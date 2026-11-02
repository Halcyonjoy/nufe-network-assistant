using System.Diagnostics;
using System.Net.NetworkInformation;
using NufeCampus.Windows;

int passed = 0, failed = 0;
void Check(string name, bool ok) { if (ok) passed++; else failed++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); }
var meter = new NetworkMeter();
long second = Stopwatch.Frequency;
var home = new NetworkConnection("wifi-1", "家里的网络", "Wi-Fi");
NetworkCounters Counter(NetworkConnection c, long sent, long received, long seconds) => new(c, sent, received, seconds * second);
Check("First non-campus Wi-Fi sample exposes the real name without inventing a speed", meter.Sample(Counter(home, 100, 200, 1)) is { Connection: var c, Rate: null } && c == home);
Check("Non-campus Wi-Fi measures upload/download bytes per second", meter.Sample(Counter(home, 2100, 8200, 3)).Rate == new TrafficRate(1000, 4000));
var other = home with { Name = "手机热点" };
Check("SSID change on the same adapter resets the baseline", meter.Sample(Counter(other, 5100, 12200, 4)) is { Connection.Name: "手机热点", Rate: null });
Check("Hotspot measures its own subsequent counters", meter.Sample(Counter(other, 6100, 14200, 5)).Rate == new TrafficRate(1000, 2000));
var ethernet = new NetworkConnection("ethernet-1", "以太网", "以太网");
Check("Switching to Ethernet resets the baseline", meter.Sample(Counter(ethernet, 500000, 900000, 6)).Rate == null);
Check("Ethernet has working rates", meter.Sample(Counter(ethernet, 501000, 903000, 7)).Rate == new TrafficRate(1000, 3000));
var vpn = new NetworkConnection("vpn-1", "VPN interface", "VPN / 隧道");
Check("VPN counters are separate from physical interface counters", meter.Sample(Counter(vpn, 1000, 2000, 8)).Rate == null && meter.Sample(Counter(vpn, 3000, 6000, 9)).Rate == new TrafficRate(2000, 4000));
Check("Disconnected state has no stale name or speed", meter.Sample(null) is { Connection: null, Rate: null });
Check("Reconnection starts a fresh baseline", meter.Sample(Counter(home, 50000, 60000, 12)).Rate == null);
Check("Counter rollover/reset does not produce negative or spiked speeds", meter.Sample(Counter(home, 1, 2, 13)).Rate == null && meter.Sample(Counter(home, 101, 202, 14)).Rate == new TrafficRate(100, 200));
Check("Duplicate timestamps do not divide by zero", meter.Sample(Counter(home, 201, 402, 14)).Rate == null);
Check("Idle network displays measured zero rather than unavailable", meter.Sample(Counter(home, 201, 402, 15)).Rate == new TrafficRate(0, 0));
meter.Reset();
Check("Resuming after suspended sampling discards the old baseline", meter.Sample(Counter(home, 50001, 60002, 100)).Rate == null);
Check("The sample after resume uses only the new interval", meter.Sample(Counter(home, 50201, 60402, 102)).Rate == new TrafficRate(100, 200));

// Real Windows adapter and route APIs; never output names, IPs, MACs, or credentials.
var first = CurrentNetwork.Read();
if (first == null)
{
    Console.WriteLine("SKIP Live adapter sampling: no connected interface");
}
else
{
    var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Id == first.Connection.InterfaceId);
    Check("Live selection is an active non-loopback interface", adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback);
    Check("Live current-network name is populated", !string.IsNullOrWhiteSpace(first.Connection.Name));
    var before = adapter.GetIPStatistics();
    var realMeter = new NetworkMeter(); realMeter.Sample(first);
    await Task.Delay(1100);
    var next = CurrentNetwork.Read();
    var after = adapter.GetIPStatistics();
    Check("Live Windows counters are read in range", next != null && next.Connection == first.Connection && next.Sent >= before.BytesSent && next.Sent <= after.BytesSent && next.Received >= before.BytesReceived && next.Received <= after.BytesReceived);
    var rate = realMeter.Sample(next).Rate;
    Check("Live second sample returns finite nonnegative rates without an agent", rate != null && double.IsFinite(rate.Upload) && double.IsFinite(rate.Download) && rate.Upload >= 0 && rate.Download >= 0);
}
Console.WriteLine($"Passed {passed}, failed {failed}");
return failed == 0 ? 0 : 1;
