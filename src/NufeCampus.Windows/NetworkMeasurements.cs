using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace NufeCampus.Windows;

public sealed record TrafficRate(double Upload, double Download);
public sealed class NetworkMeter
{
    private NetworkCounters? previous;
    public void Reset() => previous = null;
    public NetworkSample Sample() => Sample(CurrentNetwork.Read());
    public NetworkSample Sample(NetworkCounters? current)
    {
        TrafficRate? rate = null;
        if (current is { } next && previous is { } prior && next.Connection == prior.Connection && next.Timestamp > prior.Timestamp && next.Sent >= prior.Sent && next.Received >= prior.Received)
        {
            double seconds = (next.Timestamp - prior.Timestamp) / (double)Stopwatch.Frequency;
            rate = new((next.Sent - prior.Sent) / seconds, (next.Received - prior.Received) / seconds);
        }
        previous = current;
        return new(current?.Connection, rate);
    }
}
public sealed record SpeedResult(double? DownloadMbps, double? UploadMbps, double? LatencyMs, long PayloadBytes, bool Limited, string Path);

public sealed class SpeedMeasurement : IDisposable
{
    private readonly HttpClient http;
    private readonly bool direct;
    public SpeedMeasurement(bool direct, HttpMessageHandler? transport = null)
    {
        this.direct = direct;
        if (transport == null)
        {
            var handler = new SocketsHttpHandler { UseProxy = !direct, UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
            if (direct) handler.ConnectCallback = async (context, token) =>
            {
                var wifi = Wifi.Read(); if (wifi.Kind != "nufe" || wifi.Address == null) throw new IOException("Campus network not confirmed");
                if (context.DnsEndPoint.Host != "speed.cloudflare.com" || context.DnsEndPoint.Port != 443) throw new IOException("Speed destination rejected");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { socket.Bind(new IPEndPoint(IPAddress.Parse(wifi.Address), 0)); await socket.ConnectAsync(addresses[0], 443, token); return new NetworkStream(socket, true); } catch { socket.Dispose(); throw; }
            };
            transport = handler;
        }
        http = new(transport) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<SpeedResult> RunAsync(bool full, IProgress<string>? progress, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(full ? 60 : 30));
        long used = 0, perDirection = full ? 300_000_000 : 80_000_000; double? down = null, up = null, latency = null; bool limited = false;
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5)); var watch = Stopwatch.StartNew();
                using var response = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode(); latency = watch.Elapsed.TotalMilliseconds;
            }
            progress?.Report("正在测量下载…");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(full ? 22 : 10)); long bytes = 0; var watch = Stopwatch.StartNew();
                try
                {
                    using var response = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=" + perDirection, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); byte[] buffer = new byte[65536]; int read;
                    while (bytes < perDirection && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, perDirection - bytes)), deadline.Token)) > 0) { bytes += read; used += read; }
                    if (bytes == perDirection) down = bytes * 8 / watch.Elapsed.TotalSeconds / 1_000_000; else limited = true;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { limited = true;/* Partial transfers are not displayed as a completed direction. */}
            }
            progress?.Report("正在测量上传…");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(full ? 22 : 10)); var watch = Stopwatch.StartNew(); long sent = 0;
                using var payload = new SyntheticUpload(perDirection, n => { sent += n; used += n; });
                try { using var request = new HttpRequestMessage(HttpMethod.Post, "https://speed.cloudflare.com/__up") { Content = payload }; using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode(); if (sent == perDirection) up = sent * 8 / watch.Elapsed.TotalSeconds / 1_000_000; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { limited = true; }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { limited = true; }
        token.ThrowIfCancellationRequested();
        return new(down, up, latency, used, limited, direct ? "校园网源地址绑定（VPN 绕过未确认）" : "默认路径（使用系统代理设置）");
    }
    private sealed class SyntheticUpload(long length, Action<int> sent) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) { byte[] block = new byte[65536]; long done = 0; while (done < length) { int n = (int)Math.Min(block.Length, length - done); await stream.WriteAsync(block.AsMemory(0, n), token); done += n; sent(n); } }
    }
    public void Dispose() => http.Dispose();
}
