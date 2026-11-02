using System.Net;
using System.Net.Sockets;
using System.Text;
namespace NufeCampus.Windows;

public sealed class InternetProbe : IDisposable
{
    private readonly HttpClient http;
    public InternetProbe(bool direct = false)
    {
        var handler = new SocketsHttpHandler { UseProxy = !direct, UseCookies = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.Zero };
        if (direct) handler.ConnectCallback = async (_, token) =>
        {
            var wifi = Wifi.Read(); if (wifi.Kind != "nufe" || wifi.Address == null) throw new IOException("NUFE network not confirmed");
            var addresses = await Dns.GetHostAddressesAsync("www.msftconnecttest.com", AddressFamily.InterNetwork, token);
            if (addresses.Length == 0) throw new IOException("No probe address available");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { socket.Bind(new IPEndPoint(IPAddress.Parse(wifi.Address), 0)); await socket.ConnectAsync(addresses[0], 80, token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }
    public async Task<bool> CheckAsync(CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = new byte[65]; int count = 0, read;
            while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), token)) > 0) count += read;
            return Encoding.ASCII.GetString(bytes, 0, count).Trim() == "Microsoft Connect Test";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or SocketException) { return false; }
    }
    public void Dispose() => http.Dispose();
}
