using AngleSharp.Html.Parser;
using NufeCampus.Windows;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace NufeCampus.SelfService;

// Loaded only by the on-demand UI. No browser cookie import, disk cache, logs or automatic password retry.
public sealed partial class SelfClient : IDisposable
{
    public static readonly Uri Base = new("http://10.100.0.3:8080/Self/");
    private readonly HttpClient http;
    private readonly Func<WifiState> wifi;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<object> tickets = [];
    private readonly CookieContainer cookies = new();
    private Dictionary<string, string>? loginFields;
    private Uri? loginAction;
    private string account = "";
    public bool Consent { get; set; }
    public bool Authenticated { get; private set; }
    public string AuthenticatedAccount => Authenticated ? account : "";
    public bool HasLoginChallenge => loginFields != null;
    public SelfClient(HttpMessageHandler? handler = null, Func<WifiState>? network = null, Func<DateTimeOffset>? clock = null)
    {
        wifi = network ?? Wifi.Read; this.clock = clock ?? (() => DateTimeOffset.UtcNow); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (handler == null)
        {
            var sockets = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = true, CookieContainer = cookies, ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.Zero, AutomaticDecompression = DecompressionMethods.All };
            sockets.ConnectCallback = async (context, token) =>
            {
                var current = Guard(); if (context.DnsEndPoint.Host != Base.Host || context.DnsEndPoint.Port != Base.Port) throw new SelfException("protocol");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { socket.Bind(new IPEndPoint(IPAddress.Parse(current.Address!), 0)); await socket.ConnectAsync(IPAddress.Parse(Base.Host), Base.Port, token); return new NetworkStream(socket, true); } catch { socket.Dispose(); throw; }
            }; handler = sockets;
        }
        http = new(handler) { Timeout = TimeSpan.FromSeconds(12) };
    }
    private WifiState Guard() { var n = wifi(); if (n.Kind != "nufe" || n.Address == null) throw new SelfException("off_campus"); if (!Consent) throw new SelfException("consent"); return n; }
    private async Task<T> Locked<T>(Func<Task<T>> work, CancellationToken token) { await gate.WaitAsync(token); try { Guard(); return await work(); } finally { gate.Release(); } }
    private static string StripSession(string path) => Regex.Replace(path, @";jsessionid=[^/;?]*", "", RegexOptions.IgnoreCase);
    private static Uri Action(string text, string path)
    {
        var uri = new Uri(Base, text);
        if (uri.Scheme != "http" || uri.Host != Base.Host || uri.Port != Base.Port || uri.UserInfo.Length != 0 || StripSession(uri.AbsolutePath) != path || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new SelfException("protocol");
        return uri;
    }
    private async Task<string> Read(HttpResponseMessage response, CancellationToken token, int limit = 2_000_000)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); byte[] block = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(block, token)) > 0) { if (buffer.Length + read > limit) throw new SelfException("protocol"); buffer.Write(block, 0, read); }
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('\'', '"') ?? "utf-8";
        if (charset.Equals("utf8", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentType?.MediaType == "application/json") charset = "utf-8";
        return Encoding.GetEncoding(charset).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
    private async Task<string> Get(string path, CancellationToken token)
    {
        Guard(); using var response = await http.GetAsync(new Uri(Base, path), HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is >= 300 and < 400) { Authenticated = false; tickets.Clear(); throw new SelfException("session"); }
        response.EnsureSuccessStatusCode(); string text = await Read(response, token);
        if (path != "login/" && text.Contains("/Self/login/verify", StringComparison.Ordinal)) { Authenticated = false; tickets.Clear(); throw new SelfException("session"); }
        return text;
    }
    private void RequireSession() { if (!Authenticated) throw new SelfException("session"); }
    public Task<bool> LogoutAsync(CancellationToken token) => Locked(async () =>
    {
        RequireSession();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "login/logout")); request.Headers.Referrer = new(Base, "dashboard");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (302 or 303)) throw new SelfException("uncertain");
            try { await Get("dashboard/getOnlineList", token); } catch (SelfException e) when (e.Code == "session") { return true; }
            throw new SelfException("uncertain");
        }
        finally { Authenticated = false; account = ""; tickets.Clear(); loginFields = null; foreach (Cookie cookie in cookies.GetAllCookies()) cookie.Expired = true; }
    }, token);
    public Task<LoginChallenge> PrepareLoginAsync(CancellationToken token) => Locked(async () =>
    {
        Authenticated = false; tickets.Clear(); loginFields = null;
        foreach (Cookie cookie in cookies.GetAllCookies()) cookie.Expired = true;
        string html = await Get("login/", token);
        if (Regex.IsMatch(html, @"var\s+md5\s*=\s*true")) throw new SelfException("protocol");
        using var doc = new HtmlParser().ParseDocument(html);
        var form = doc.QuerySelector("form[action]") ?? throw new SelfException("protocol");
        if (!string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase)) throw new SelfException("protocol");
        loginAction = Action(form.GetAttribute("action") ?? "", "/Self/login/verify");
        loginFields = [];
        foreach (var input in form.QuerySelectorAll("input[name]"))
        {
            string name = input.GetAttribute("name")!;
            if (name is "account" or "password" or "code") { loginFields[name] = ""; continue; }
            if (name is "foo" or "bar" or "checkcode" or "csrftoken") loginFields[name] = input.GetAttribute("value") ?? "";
        }
        if (!loginFields.ContainsKey("account") || !loginFields.ContainsKey("password")) throw new SelfException("protocol");
        var captcha = doc.QuerySelector("#randomDiv"); bool required = captcha != null && !captcha.ClassList.Contains("hide") && !(captcha.GetAttribute("style") ?? "").Contains("display:none", StringComparison.OrdinalIgnoreCase);
        using var image = await http.GetAsync(new Uri(Base, "login/randomCode?t=" + clock().ToUnixTimeMilliseconds()), HttpCompletionOption.ResponseHeadersRead, token);
        image.EnsureSuccessStatusCode(); using var imageStream = await image.Content.ReadAsStreamAsync(token); using var imageBuffer = new MemoryStream(); byte[] data = new byte[4096]; int length;
        while ((length = await imageStream.ReadAsync(data, token)) > 0) { if (imageBuffer.Length + length > 200_000) throw new SelfException("protocol"); imageBuffer.Write(data, 0, length); }
        return new LoginChallenge(required, imageBuffer.ToArray());
    }, token);
    public Task<bool> LoginAsync(SecretPair secret, string code, CancellationToken token) => Locked(async () =>
    {
        if (loginFields == null || loginAction == null) throw new SelfException("expired");
        var fields = loginFields; loginFields = null; fields["account"] = secret.Account; fields["password"] = secret.Password; fields["code"] = code;
        using var request = new HttpRequestMessage(HttpMethod.Post, loginAction) { Content = new FormUrlEncodedContent(fields) }; fields["password"] = "";
        request.Headers.Referrer = new(Base, "login/"); request.Headers.Add("Origin", Base.GetLeftPart(UriPartial.Authority));
        Guard(); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // Never follow redirects or replay credentials, even on 307/308. A protected read establishes success.
        if ((int)response.StatusCode is 307 or 308 || (int)response.StatusCode >= 400) throw new SelfException("login");
        string body = await Read(response, token);
        try { using var rows = JsonDocument.Parse(await Get("dashboard/getOnlineList", token)); if (rows.RootElement.ValueKind != JsonValueKind.Array) throw new SelfException("login"); }
        catch (SelfException)
        {
            using var doc = new HtmlParser().ParseDocument(body); string tip = doc.QuerySelector("#errorTip .alert")?.TextContent ?? "";
            throw new SelfException(tip.Contains("验证码") || tip.Contains("校验码") ? "captcha" : "login");
        }
        catch (JsonException) { throw new SelfException("login"); }
        account = secret.Account; Authenticated = true; return true;
    }, token);
    private async Task<IReadOnlyList<OnlineDevice>> Online(CancellationToken token)
    {
        RequireSession(); using var json = JsonDocument.Parse(await Get("dashboard/getOnlineList", token));
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 500) throw new SelfException("protocol");
        return json.RootElement.EnumerateArray().Select(r =>
        {
            string sid = SelfData.Value(r, "sessionId"), mac = SelfData.Value(r, "mac"); if (sid.Length == 0 || Wifi.NormalizeMac(mac).Length != 12) throw new SelfException("protocol");
            decimal.TryParse(SelfData.Value(r, "downFlow"), out var down); decimal.TryParse(SelfData.Value(r, "upFlow"), out var up);
            return new OnlineDevice(sid, mac, SelfData.Value(r, "ip"), SelfData.Date(SelfData.Value(r, "loginTime")), SelfData.Decimal(SelfData.Value(r, "useTime"), 60), SelfData.Decimal((down + up).ToString(System.Globalization.CultureInfo.InvariantCulture), 1024), SelfData.Value(r, "hostName"), SelfData.Value(r, "terminalType"), SelfData.Decimal(SelfData.Value(r, "upFlow"), 1024), SelfData.Decimal(SelfData.Value(r, "downFlow"), 1024));
        }).ToArray();
    }
    public Task<IReadOnlyList<OnlineDevice>> OnlineAsync(CancellationToken token) => Locked(() => Online(token), token);
    public Task<Page<HistoryRow>> HistoryAsync(DateOnly start, DateOnly end, int page, CancellationToken token) => Locked(async () =>
    {
        RequireSession(); SelfData.Range(start, end); if (page < 1 || page > 1000) throw new SelfException("page");
        using var json = JsonDocument.Parse(await Get($"bill/getUserOnlineLog?pageNumber={page}&pageSize=10&startTime={start:yyyy-MM-dd}&endTime={end:yyyy-MM-dd}&sortName=loginTime&sortOrder=DESC", token));
        var root = json.RootElement; var rows = Rows(root); return new Page<HistoryRow>(Total(root), rows.EnumerateArray().Select(r => new HistoryRow(SelfData.Date(SelfData.Value(r, "loginTime")), SelfData.Date(SelfData.Value(r, "logoutTime")), SelfData.Value(r, "userIp"), SelfData.Value(r, "macAddress"), SelfData.Value(r, "time"), SelfData.Value(r, "flow"), SelfData.Value(r, "costMoney"), SelfData.Value(r, "internetUpFlow"), SelfData.Value(r, "internetDownFlow"), SelfData.Value(r, "chinanetUpFlow"), SelfData.Value(r, "chinanetDownFlow"), SelfData.Value(r, "nasIp"), SelfData.Value(r, "nasPort"))).ToArray(), Summary(root, ["TIME", "FLOW", "COSTMONEY", "INTERNETUPFLOW", "INTERNETDOWNFLOW", "CHINANETUPFLOW", "CHINANETDOWNFLOW"]), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rows.GetRawText()))));
    }, token);
    public Task<Page<BillRow>> BillsAsync(int year, int page, CancellationToken token) => Locked(async () =>
    {
        RequireSession(); if (year < 2000 || year > 2100 || page < 1 || page > 1000) throw new SelfException("page");
        using var json = JsonDocument.Parse(await Get($"bill/getMonthPay?pageNumber={page}&pageSize=10&year={year}&sortName=0&sortOrder=DESC", token)); var root = json.RootElement; var rows = Rows(root);
        return new Page<BillRow>(Total(root), rows.EnumerateArray().Select(r => { if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 8) throw new SelfException("protocol"); var v = r.EnumerateArray().Select(SelfData.Scalar).ToArray(); return new BillRow(SelfData.Date(v[0]), SelfData.Date(v[1]), v[2], v[3], v[4], v[5], v[6], SelfData.Date(v[7])); }).ToArray(), Summary(root, ["USETIME", "USEBASEMONEY", "USEFLOW", "USEDMONEY"]));
    }, token);
    private static int Total(JsonElement root) { if (!int.TryParse(SelfData.Value(root, "total"), out var total) || total < 0 || total > 1_000_000) throw new SelfException("protocol"); return total; }
    private static JsonElement Rows(JsonElement root) { if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10) throw new SelfException("protocol"); return rows; }
    private static string Summary(JsonElement root, string[] keys)
    {
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object) return "暂无汇总";
        return string.Join(" · ", keys.Select(k =>
        {
            var (label, unit) = k switch { "TIME" or "USETIME" => ("时长", "分钟"), "FLOW" or "USEFLOW" => ("流量", "MB"), "COSTMONEY" => ("金额", "元"), "USEBASEMONEY" => ("基本费用", "元"), "USEDMONEY" => ("使用费用", "元"), "INTERNETUPFLOW" => ("国际上行", "MB"), "INTERNETDOWNFLOW" => ("国际下行", "MB"), "CHINANETUPFLOW" => ("国内上行", "MB"), "CHINANETDOWNFLOW" => ("国内下行", "MB"), _ => ("其他", "") };
            string value = SelfData.Value(summary, k); return label + "：" + (value.Length == 0 ? "—" : value) + " " + unit;
        }));
    }
    public Task<AccountOverview> OverviewAsync(CancellationToken token) => Locked(async () =>
    {
        RequireSession(); using var doc = new HtmlParser().ParseDocument(await Get("dashboard", token));
        string scripts = string.Join('\n', doc.QuerySelectorAll("script:not([src])").Select(s => s.TextContent));
        string Field(string key) { var match = Regex.Match(scripts, "[\"']?" + key + "[\"']?\\s*:\\s*(?<value>\"(?:[^\"\\\\]|\\\\.)*\"|'[^'\\r\\n]*'|[-+0-9.]+|true|false|null)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); if (!match.Success) return "未识别"; string v = match.Groups["value"].Value; if (v.StartsWith('"')) { try { return SelfData.Limit(JsonSerializer.Deserialize<string>(v) ?? ""); } catch (JsonException) { return "未识别"; } } return SelfData.Limit(v.Trim('\'')); }
        string name = Field("userName"); if (name != "未识别" && name != account) throw new SelfException("account");
        return new AccountOverview(account, Field("useFlag"), Field("serviceDefault"), Field("useTime"), Field("useFlow"), Field("leftMoney"));
    }, token);
    private sealed record Form(Uri Action, string Token, string Ajax, OperatorState State);
    private async Task<Form> OperatorForm(CancellationToken token)
    {
        RequireSession(); using var doc = new HtmlParser().ParseDocument(await Get("service/operatorId", token));
        var forms = doc.QuerySelectorAll("form[action]").Where(f => StripSession(new Uri(Base, f.GetAttribute("action")!).AbsolutePath) == "/Self/service/bind-operator").ToArray();
        if (forms.Length != 1 || !string.Equals(forms[0].GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase)) throw new SelfException("protocol");
        var action = Action(forms[0].GetAttribute("action")!, "/Self/service/bind-operator"); var inputs = forms[0].QuerySelectorAll("input[name]").ToDictionary(n => n.GetAttribute("name")!);
        string csrf = inputs.TryGetValue("csrftoken", out var cf) ? cf.GetAttribute("value") ?? "" : ""; if (csrf.Length == 0) throw new SelfException("protocol");
        var names = new[] { "南京移动", "镇江移动", "南京电信" }; var groups = new List<OperatorGroup>();
        for (int i = 0; i < 3; i++) { if (!inputs.TryGetValue("FLDEXTRA" + (2 * i + 1), out var a) || !inputs.TryGetValue("FLDEXTRA" + (2 * i + 2), out var p)) throw new SelfException("protocol"); groups.Add(new(names[i], a.GetAttribute("value") ?? "", !string.IsNullOrEmpty(p.GetAttribute("value")))); }
        string scripts = string.Join('\n', doc.QuerySelectorAll("script:not([src])").Select(s => s.TextContent)); var m = Regex.Match(scripts, "(?:window\\.)?AJAXCSRFTOKEN\\s*=\\s*(?<q>['\"])(?<token>[^'\"\\\\\r\n]*)\\k<q>");
        if (!m.Success && Regex.IsMatch(scripts, @"AJAXCSRFTOKEN\s*=\s*(?!null\b|undefined\b)")) throw new SelfException("protocol");
        return new(action, csrf, m.Success ? m.Groups["token"].Value : "", new(groups));
    }
    public Task<OperatorState> OperatorsAsync(CancellationToken token) => Locked(async () => (await OperatorForm(token)).State, token);
    public Task<OfflineTicket> PrepareOfflineAsync(OnlineDevice selected, CancellationToken token) => Locked(async () =>
    {
        var rows = await Online(token); if (rows.Count(d => Same(d, selected)) != 1) throw new SelfException("changed"); var ticket = new OfflineTicket(selected, clock().AddSeconds(45)); tickets.Clear(); tickets.Add(ticket); return ticket;
    }, token);
    private static bool Same(OnlineDevice a, OnlineDevice b) => a.SessionId == b.SessionId && Wifi.NormalizeMac(a.Mac) == Wifi.NormalizeMac(b.Mac) && a.Ip == b.Ip && a.LoginTime == b.LoginTime;
    private void Consume(object ticket, DateTimeOffset expires) { if (!tickets.Remove(ticket) || clock() > expires) throw new SelfException("expired"); }
    public Task<bool> OfflineAsync(OfflineTicket ticket, CancellationToken token, Func<bool>? beforeSubmit = null) => Locked(async () =>
    {
        Consume(ticket, ticket.Expires); var current = await Online(token); if (current.Count(d => Same(d, ticket.Device)) != 1) throw new SelfException("changed"); var form = await OperatorForm(token);
        using var encoded = new FormUrlEncodedContent(new Dictionary<string, string> { { "sessionid", ticket.Device.SessionId }, { "ajaxCsrfToken", form.Ajax }, { "t", clock().ToUnixTimeMilliseconds().ToString() } });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "dashboard/tooffline?" + await encoded.ReadAsStringAsync(token))); request.Headers.Referrer = new(Base, "dashboard"); request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Guard(); if (beforeSubmit != null && !beforeSubmit()) throw new SelfException("changed"); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); if (!response.IsSuccessStatusCode) throw new SelfException("uncertain");
        var after = await Online(token); if (after.Any(d => d.SessionId == ticket.Device.SessionId)) throw new SelfException("uncertain"); return true;
    }, token);
    public Task<BindingTicket> PrepareBindingAsync(bool unbind, string phone, string password, CancellationToken token, int group = 0) => Locked(async () =>
    {
        if (group is < 0 or > 2 || phone.Length is < 1 or > 64 || phone.Any(char.IsControl) || password.Length is < 1 or > 256) throw new SelfException("broadband"); var form = await OperatorForm(token); if (!form.State.CanWrite(group)) throw new SelfException("other_groups");
        if (unbind && (!form.State.Groups[group].Active || form.State.Groups[group].Account != phone)) throw new SelfException("changed"); var ticket = new BindingTicket(form.State, unbind, phone, clock().AddSeconds(45), group); tickets.Clear(); tickets.Add(ticket); return ticket;
    }, token);
    public Task<bool> BindAsync(BindingTicket ticket, string password, CancellationToken token) => Locked(async () =>
    {
        Consume(ticket, ticket.Expires); if (password.Length is < 1 or > 256) throw new SelfException("broadband"); var form = await OperatorForm(token); if (!form.State.CanWrite(ticket.Group)) throw new SelfException("other_groups"); if (form.State.Fingerprint != ticket.Before.Fingerprint) throw new SelfException("changed");
        var fields = Enumerable.Range(1, 6).ToDictionary(n => "FLDEXTRA" + n, _ => ""); fields["csrftoken"] = form.Token; if (!ticket.Unbind) { fields["FLDEXTRA" + (ticket.Group * 2 + 1)] = ticket.Account; fields["FLDEXTRA" + (ticket.Group * 2 + 2)] = password; }
        using var request = new HttpRequestMessage(HttpMethod.Post, form.Action) { Content = new FormUrlEncodedContent(fields) }; fields["FLDEXTRA" + (ticket.Group * 2 + 2)] = ""; request.Headers.Referrer = new(Base, "service/operatorId"); request.Headers.Add("Origin", Base.GetLeftPart(UriPartial.Authority));
        Guard(); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (302 or 303)) throw new SelfException("uncertain");
        var after = await OperatorForm(token); if (!after.State.CanWrite(ticket.Group) || (ticket.Unbind ? after.State.Groups[ticket.Group].Active : after.State.Groups[ticket.Group].Account != ticket.Account || !after.State.Groups[ticket.Group].HasPassword)) throw new SelfException("uncertain"); return true;
    }, token);
    private async Task<Page<BoundDevice>> BoundDevices(int page, CancellationToken token)
    {
        RequireSession(); if (page is < 1 or > 1000) throw new SelfException("page");
        using var json = JsonDocument.Parse(await Get($"service/getMacList?pageNumber={page}&pageSize=10&sortName=0&sortOrder=DESC", token));
        var root = json.RootElement;
        var rows = Rows(root).EnumerateArray().Select(r =>
        {
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 5) throw new SelfException("protocol");
            var v = r.EnumerateArray().Select(SelfData.Scalar).ToArray(); if (Wifi.NormalizeMac(v[1]).Length != 12) throw new SelfException("protocol");
            return new BoundDevice(v[0], v[1], v[2], SelfData.Date(v[3]), v[4]);
        }).ToArray(); return new(Total(root), rows, "");
    }
    public Task<Page<BoundDevice>> BoundDevicesAsync(int page, CancellationToken token) => Locked(() => BoundDevices(page, token), token);
    private async Task<BoundDevice[]> AllBound(CancellationToken token)
    {
        var first = await BoundDevices(1, token); if (first.Total > 1000) throw new SelfException("too_many"); var rows = first.Rows.ToList();
        for (int page = 2; rows.Count < first.Total; page++) { var next = await BoundDevices(page, token); if (next.Total != first.Total || next.Rows.Count == 0) throw new SelfException("changed"); rows.AddRange(next.Rows); }
        return rows.ToArray();
    }
    public Task<MacTicket> PrepareMacRemovalAsync(BoundDevice device, CancellationToken token) => Locked(async () =>
    {
        if ((await AllBound(token)).Count(d => d == device) != 1) throw new SelfException("changed"); var ticket = new MacTicket(device, clock().AddSeconds(45)); tickets.Clear(); tickets.Add(ticket); return ticket;
    }, token);
    public Task<bool> RemoveMacAsync(MacTicket ticket, CancellationToken token) => Locked(async () =>
    {
        Consume(ticket, ticket.Expires); if ((await AllBound(token)).Count(d => d == ticket.Device) != 1) throw new SelfException("changed"); var form = await OperatorForm(token);
        using var encoded = new FormUrlEncodedContent(new Dictionary<string, string> { { "mac", ticket.Device.Mac }, { "ajaxCsrfToken", form.Ajax } });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "service/unbindmac?" + await encoded.ReadAsStringAsync(token)));
        request.Headers.Referrer = new(Base, "service/myMac"); request.Headers.Add("X-Requested-With", "XMLHttpRequest"); Guard(); using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode || (await AllBound(token)).Any(d => Wifi.NormalizeMac(d.Mac) == Wifi.NormalizeMac(ticket.Device.Mac))) throw new SelfException("uncertain"); return true;
    }, token);
    public Task<PlatformFeature[]> FeaturesAsync(CancellationToken token) => Locked(async () =>
    {
        RequireSession();
        var known = new Dictionary<string, (string Name, bool Builtin)>
        {
            ["/Self/dashboard"] = ("账号概况与在线设备", true),
            ["/Self/bill/userOnlineLog"] = ("上网记录", true),
            ["/Self/bill/monthPay"] = ("历史账单", true),
            ["/Self/bill/operatorLog"] = ("业务办理记录", true),
            ["/Self/service/myMac"] = ("我的绑定设备", true),
            ["/Self/service/operatorId"] = ("绑定运营商账号", true),
            ["/Self/service/goStop"] = ("账号报停（影响账号状态及计费）", true),
            ["/Self/service/goReopen"] = ("账号复通（影响账号状态及计费）", true),
            ["/Self/unlogin/help"] = ("使用帮助", true),
            ["/Self/unlogin/agreement"] = ("服务协议", true)
        };
        var features = new List<PlatformFeature>();
        // Only these verified read-only menu pages are fetched; never crawl their action links.
        foreach (string page in new[] { "dashboard", "bill", "service" })
        {
            using var doc = new HtmlParser().ParseDocument(await Get(page, token));
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                if (!Uri.TryCreate(Base, a.GetAttribute("href"), out var uri) || uri.Scheme != "http" || uri.Host != Base.Host || uri.Port != Base.Port || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) continue;
                string path = StripSession(uri.AbsolutePath); if (known.TryGetValue(path, out var feature)) features.Add(new(feature.Name, new Uri(Base, path).AbsoluteUri, feature.Builtin));
            }
        }
        return features.Distinct().ToArray();
    }, token);
    public void Dispose() { loginFields = null; account = ""; tickets.Clear(); http.Dispose(); }
}
