using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace NufeCampus.Windows;

public sealed record PortalState(bool Online, string Account, string Mac);
public sealed record LoginResult(bool Success, string Code, string ServerMessage = "");
public interface ICampusPortal : IDisposable
{
    Task<PortalState> ReadAsync(CancellationToken token);
    Task<LoginResult> AuthenticateAsync(SecretPair secret, string carrier, CancellationToken token);
    Task LogoutAsync(SecretPair secret, string carrier, CancellationToken token);
    bool Matches(PortalState state, string account, string carrier);
}
public sealed class CampusPortal : ICampusPortal
{
    private const string Host = "10.200.253.5";
    private const string Callback = "campusProbe";
    private readonly HttpClient http;
    private readonly Func<WifiState> wifi;
    public CampusPortal() : this(null, Wifi.Read) { }
    public CampusPortal(HttpMessageHandler? handler, Func<WifiState> wifi)
    {
        this.wifi = wifi; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (handler == null)
        {
            var sockets = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectTimeout = TimeSpan.FromSeconds(3)
            };
            sockets.ConnectCallback = async (context, token) =>
            {
                var current = Guard();
                if (context.DnsEndPoint.Host != Host || context.DnsEndPoint.Port != 80 || current.Address == null) throw new IOException("Campus destination rejected");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { socket.Bind(new IPEndPoint(IPAddress.Parse(current.Address), 0)); await socket.ConnectAsync(IPAddress.Parse(Host), 80, token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            };
            handler = sockets;
        }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }
    private WifiState Guard()
    {
        var current = wifi();
        if (current.Kind != "nufe" || current.Address == null) throw new IOException("NUFE-STU with IPv4 not confirmed");
        return current;
    }
    private async Task<JsonDocument> Get(string path, CancellationToken token)
    {
        Guard();
        using var response = await http.GetAsync("http://" + Host + path, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); byte[] block = new byte[2048]; int read;
        while ((read = await stream.ReadAsync(block, token)) > 0) { if (buffer.Length + read > 32768) throw new InvalidDataException("Portal response too large"); buffer.Write(block, 0, read); }
        string charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'') ?? "gbk";
        if (charset.Equals("utf8", StringComparison.OrdinalIgnoreCase)) charset = "utf-8";
        string text = Encoding.GetEncoding(charset).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).Trim();
        if (text.EndsWith(';')) text = text[..^1].TrimEnd();
        string prefix = Callback + "(";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(')')) throw new InvalidDataException("Portal framing changed");
        var document = JsonDocument.Parse(text[prefix.Length..^1]);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException("Portal object expected"); }
        return document;
    }
    private static string String(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public async Task<PortalState> ReadAsync(CancellationToken token)
    {
        using var json = await Get("/drcom/chkstatus?callback=" + Callback, token); var root = json.RootElement;
        if (!root.TryGetProperty("result", out var value) || value.ToString() is not ("0" or "1")) throw new InvalidDataException("Portal status contract changed");
        return new(value.ToString() == "1", String(root, "uid"), String(root, "olmac"));
    }
    public bool Matches(PortalState state, string account, string carrier)
    {
        var current = Guard(); string mac = Wifi.NormalizeMac(state.Mac);
        return state.Online && state.Account == account + carrier && mac.Length == 12 && mac == Wifi.NormalizeMac(current.Mac ?? "");
    }
    public static string Classify(JsonElement root)
    {
        string text = string.Join(" ", new[] { "msg", "msga", "message" }.Select(k => String(root, k)));
        text = WebUtility.UrlDecode(text);
        if (text.Contains("Rad:Passwd_Err", StringComparison.OrdinalIgnoreCase)) return "carrier_credentials";
        if (text.Contains("Rad:Status_Err", StringComparison.OrdinalIgnoreCase)) return "carrier_state";
        if (text.Contains("Rad:UserName_Err", StringComparison.OrdinalIgnoreCase)) return "carrier_account";
        if (text.Contains("Rad:Limit Users Err", StringComparison.OrdinalIgnoreCase)) return "carrier_quota";
        if (text.Contains("In use", StringComparison.OrdinalIgnoreCase)) return "quota";
        if (text.Contains("userid error1", StringComparison.OrdinalIgnoreCase)) return "campus_missing";
        if (text.Contains("ldap auth error", StringComparison.OrdinalIgnoreCase) || new[] { "userid error1", "userid error2", "userid error3" }.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase))) return "campus_credentials";
        if (text.Contains("already online", StringComparison.OrdinalIgnoreCase) || text.Contains("已经在线", StringComparison.Ordinal)) return "already_online";
        if (text.Contains("密码", StringComparison.Ordinal) && (text.Contains("错误", StringComparison.Ordinal) || text.Contains("不正确", StringComparison.Ordinal)))
            return text.Contains("宽带", StringComparison.Ordinal) || text.Contains("运营商", StringComparison.Ordinal) ? "carrier_credentials" : "campus_credentials";
        return "business_rejected";
    }
    public async Task<LoginResult> AuthenticateAsync(SecretPair secret, string carrier, CancellationToken token)
    {
        Guard(); if (carrier is not ("@cmcc" or "@njxy")) throw new InvalidDataException("Carrier rejected");
        using var encoded = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["callback"] = Callback,
            ["DDDDD"] = secret.Account + carrier,
            ["upass"] = secret.Password,
            ["0MKKey"] = "123456",
            ["R1"] = "0",
            ["R3"] = "0",
            ["R6"] = "0",
            ["para"] = "00",
            ["v6ip"] = ""
        });
        using var json = await Get("/drcom/login?" + await encoded.ReadAsStringAsync(token), token);
        var root = json.RootElement;
        if (!root.TryGetProperty("result", out var marker) || marker.ToString() is not ("0" or "1" or "ok")) throw new InvalidDataException("Portal login contract changed");
        bool accepted = root.TryGetProperty("result", out var result) && result.ToString() is "1" or "ok";
        string code = accepted ? "ok" : Classify(root);
        if (!accepted && code != "already_online") return new(false, code, SafeMessage(root, secret));
        for (int i = 0; i < 5; i++) { if (Matches(await ReadAsync(token), secret.Account, carrier)) return new(true, "ok"); await Task.Delay(500, token); }
        return new(false, "unknown_response");
    }
    public async Task LogoutAsync(SecretPair secret, string carrier, CancellationToken token)
    {
        var state = await ReadAsync(token);
        if (!state.Online) return;
        if (!Matches(state, secret.Account, carrier)) throw new InvalidDataException("Own-account terminal not confirmed");
        using var response = await Get("/drcom/logout?callback=" + Callback, token);
    }
    public void Dispose() => http.Dispose();
    public static string SafeMessage(JsonElement root, SecretPair secret)
    {
        string text = WebUtility.UrlDecode(string.Join(" · ", new[] { "msg", "msga", "message" }.Select(k => String(root, k)).Where(s => s.Length > 0)));
        foreach (string value in new[] { secret.Password, secret.Account, WebUtility.UrlDecode(secret.Password), Convert.ToBase64String(Encoding.UTF8.GetBytes(secret.Password)) }) if (value.Length > 0) text = text.Replace(value, "[已隐藏]", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(password|upass|cookie|token|csrftoken|sessionid)\s*[:=]\s*[^\s·]+", "$1=[已隐藏]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b\d{1,3}(?:\.\d{1,3}){3}\b|\b[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}\b|[A-Za-z0-9+/=_-]{20,}|\d{6,}", "[已隐藏]");
        text = new string(text.Where(c => !char.IsControl(c)).Take(512).ToArray());
        return text;
    }
}
