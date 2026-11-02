using NufeCampus.SelfService;
using NufeCampus.Windows;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Globalization;

if (args is ["--responsiveness-only"])
{
    using var release = new ManualResetEventSlim();
    int caller = Environment.CurrentManagedThreadId, guardsOnCaller = 0;
    using var client = new SelfClient(new Fixture(), () =>
    {
        if (Environment.CurrentManagedThreadId == caller) Interlocked.Increment(ref guardsOnCaller);
        release.Wait(TimeSpan.FromMilliseconds(300)); // A slow WLAN service must not block input.
        return new("nufe", "192.0.2.1", "001122334455");
    })
    { Consent = true };
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var pending = client.PrepareLoginAsync(CancellationToken.None);
    long initiationMs = watch.ElapsedMilliseconds;
    bool responsive = initiationMs < 150 && !pending.IsCompleted;
    release.Set();
    var challenge = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    bool guardedOffCaller = guardsOnCaller == 0 && challenge.Captcha.Length > 0;
    Console.WriteLine($"{(responsive ? "PASS" : "FAIL")} Request returns before slow WLAN work completes ({initiationMs} ms)");
    Console.WriteLine($"{(guardedOffCaller ? "PASS" : "FAIL")} WLAN guards and login parsing complete away from the caller thread");
    return responsive && guardedOffCaller ? 0 : 1;
}

var checks = new List<(string, bool)>(); var none = CancellationToken.None;
void Check(string name, bool value) { checks.Add((name, value)); Console.WriteLine((value ? "PASS " : "FAIL ") + name); }
async Task<bool> Reject(Func<Task> work, string code) { try { await work(); return false; } catch (SelfException e) { return e.Code == code; } }
SelfClient Client(Fixture f, Func<WifiState>? wifi = null, Func<DateTimeOffset>? clock = null) => new(f, wifi ?? (() => new("nufe", "192.0.2.1", "001122334455")), clock) { Consent = true };
async Task Login(SelfClient c) { await c.PrepareLoginAsync(none); await c.LoginAsync(new("14000001", "synthetic p+&%=value"), "AB12", none); }
var fixture = new Fixture(); using (var c = Client(fixture))
{
    var challenge = await c.PrepareLoginAsync(none); Check("Fresh login form and bounded captcha loaded", challenge.Captcha.Length > 0 && !challenge.CaptchaRequired);
    await c.LoginAsync(new("14000001", "synthetic p+&%=value"), "AB12", none);
    Check("Native login preserves opaque fields and encodes credentials once", c.Authenticated && fixture.LoginPosts == 1 && fixture.SafeLogin);
    Check("Login is single-use without preparing again", await Reject(() => c.LoginAsync(new("14000001", "unused"), "", none), "expired") && fixture.LoginPosts == 1);
    var devices = await c.OnlineAsync(none); Check("Online device time and flow units match deployed frontend", devices.Count == 1 && devices[0].Minutes == "2" && devices[0].FlowMb == "1");
    Check("Accessible device row name never includes operation session identifier", !devices[0].ToString().Contains(devices[0].SessionId));
    var p1 = await c.HistoryAsync(new(2026, 10, 1), new(2026, 10, 30), 1, none); var p2 = await c.HistoryAsync(new(2026, 10, 1), new(2026, 10, 30), 2, none);
    Check("History uses explicit filters, page numbers and whitelisted row fields", p1.Total == 11 && p1.Rows.Count == 10 && p2.Rows.Count == 1 && fixture.HistoryQueries && p1.Rows[0].Ip == "192.0.2.2" && !JsonSerializer.Serialize(p1).Contains("private-phone"));
    var bills = await c.BillsAsync(2026, 1, none); Check("Eight-column bills mapped with monetary and usage columns", bills.Rows.Count == 1 && bills.Rows[0].Plan == "Synthetic plan" && bills.Rows[0].BaseCost == "10" && bills.Rows[0].FlowMb == "3");
    var a = await c.OverviewAsync(none); Check("Account overview excludes password and decodes safe strings", a.Account == "14000001" && a.Plan == "Synthetic plan" && !JsonSerializer.Serialize(a).Contains("server-secret"));
    var op = await c.OperatorsAsync(none); Check("Operator preview only models password presence and masks account", op.MobileWritable && op.Groups[0].Display == "***0000" && !JsonSerializer.Serialize(op).Contains("server-secret"));
    var ticket = await c.PrepareOfflineAsync(devices[0], none); fixture.IncrementUsage = true; await c.OfflineAsync(ticket, none); Check("Changing usage counters do not invalidate the same session identity", fixture.Offlines == 1 && (await c.OnlineAsync(none)).Count == 0);
    Check("Consumed offline confirmation cannot submit twice", await Reject(() => c.OfflineAsync(ticket, none), "expired") && fixture.Offlines == 1);
    var binding = await c.PrepareBindingAsync(false, "13800000000", "local-broadband", none); await c.BindAsync(binding, "local-broadband", none);
    Check("Binding posts full guarded form with only locally supplied password", fixture.BindingPosts == 1 && fixture.SafeBinding);
    Check("Consumed binding confirmation cannot submit twice", await Reject(() => c.BindAsync(binding, "local-broadband", none), "expired") && fixture.BindingPosts == 1);
    var down = await c.PrepareBindingAsync(true, "13800000000", "local-broadband", none); await c.BindAsync(down, "local-broadband", none); Check("Unbind clears sole mobile group and checks fresh state", !(await c.OperatorsAsync(none)).Groups[0].Active && fixture.BindingPosts == 2);
    var restore = await c.PrepareBindingAsync(false, "13800000000", "local-broadband", none); await c.BindAsync(restore, "local-broadband", none); Check("Explicit restore binds empty mobile group without changing other groups", (await c.OperatorsAsync(none)).Groups[0].Active && fixture.BindingPosts == 3 && fixture.SafeBinding);
    int before = fixture.Calls; Check("Invalid history range rejected before network", await Reject(() => c.HistoryAsync(new(2026, 1, 1), new(2026, 10, 30), 1, none), "range") && fixture.Calls == before);
    Check("Invalid page and year rejected before network", await Reject(() => c.BillsAsync(1990, 0, none), "page") && fixture.Calls == before);
    fixture.Expired = true; Check("Expired self session stops query and clears authenticated state", await Reject(() => c.OnlineAsync(none), "session") && !c.Authenticated && fixture.LoginPosts == 1);
}
foreach (string mode in new[] { "foreign_login", "md5_changed", "missing_password" }) { var f = new Fixture { Mode = mode }; using var c = Client(f); Check("Login protocol guard " + mode, await Reject(() => c.PrepareLoginAsync(none), "protocol") && f.LoginPosts == 0); }
foreach (string mode in new[] { "other_group", "password_only_other", "foreign_binding", "missing_csrf", "wrong_unbind" }) { var f = new Fixture(); using var c = Client(f); await Login(c); f.Mode = mode; string expected = mode is "other_group" or "password_only_other" ? "other_groups" : mode == "wrong_unbind" ? "changed" : "protocol"; Check("No write on unsafe binding state " + mode, await Reject(() => c.PrepareBindingAsync(true, mode == "wrong_unbind" ? "13800000001" : "13800000000", "local", none), expected) && f.BindingPosts == 0); }
{ var f = new Fixture(); using var c = Client(f); await Login(c); var t = await c.PrepareBindingAsync(false, "13800000000", "local", none); f.Mode = "changed_account"; Check("Fresh binding state change blocks confirmed write", await Reject(() => c.BindAsync(t, "local", none), "changed") && f.BindingPosts == 0); }
{ var f = new Fixture(); using var c = Client(f); await Login(c); var d = (await c.OnlineAsync(none))[0]; var t = await c.PrepareOfflineAsync(d, none); f.Mode = "changed_session"; Check("Reused session identifier with different MAC is not logged out", await Reject(() => c.OfflineAsync(t, none), "changed") && f.Offlines == 0); }
{ var now = DateTimeOffset.UtcNow; var f = new Fixture(); using var c = Client(f, clock: () => now); await Login(c); var t = await c.PrepareBindingAsync(false, "13800000000", "local", none); now = now.AddSeconds(46); Check("Expired confirmation never submits", await Reject(() => c.BindAsync(t, "local", none), "expired") && f.BindingPosts == 0); }
{ var f = new Fixture(); using var c = Client(f); await Login(c); var t = await c.PrepareBindingAsync(true, "13800000000", "local", none); f.Mode = "lost_response"; bool lost = false; try { await c.BindAsync(t, "local", none); } catch (HttpRequestException) { lost = true; } var state = await c.OperatorsAsync(none); Check("Lost write response only permits explicit readback, never automatic replay", lost && !state.Groups[0].Active && f.BindingPosts == 1 && await Reject(() => c.BindAsync(t, "local", none), "expired")); }
foreach (string mode in new[] { "html_session", "bad_json", "too_many_rows" }) { var f = new Fixture(); using var c = Client(f); await Login(c); f.Mode = mode; bool rejected = false; try { await c.HistoryAsync(new(2026, 10, 1), new(2026, 10, 30), 1, none); } catch (Exception e) when (e is SelfException or JsonException) { rejected = true; } Check("Unexpected protected response not displayed " + mode, rejected); }
{ var f = new Fixture(); using var c = Client(f, () => new("off_campus")); Check("Off-campus blocks every request including credential preparation", await Reject(() => c.PrepareLoginAsync(none), "off_campus") && f.Calls == 0); }
{ var f = new Fixture(); using var c = Client(f); c.Consent = false; Check("HTTP consent required before any self request", await Reject(() => c.PrepareLoginAsync(none), "consent") && f.Calls == 0); }
{ var f = new Fixture { Mode = "wrong_password" }; using var c = Client(f); await c.PrepareLoginAsync(none); Check("Rejected self login does not retry or reuse challenge", await Reject(() => c.LoginAsync(new("14000001", "bad"), "", none), "login") && f.LoginPosts == 1 && await Reject(() => c.LoginAsync(new("14000001", "bad"), "", none), "expired")); }

string root = Path.Combine(Path.GetTempPath(), "NufeCampus-self-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    string path = Path.Combine(root, "export.csv"); var f = new Fixture(); using var c = Client(f); await Login(c);
    await CsvExporter.WriteAsync(path, ["Time", "IP"], (p, t) => c.HistoryAsync(new(2026, 10, 1), new(2026, 10, 30), p, t), r => [r.LoginTime, r.Ip], none);
    var csv = await File.ReadAllTextAsync(path); Check("Real CSV file exports every filtered page, not only visible page", File.ReadAllLines(path).Length == 12 && csv.Contains("192.0.2.2") && !Directory.EnumerateFiles(root, "*.partial-*").Any());
    Check("CSV escapes formula injection, quotes and multiline text", SelfData.CsvCell(" =HYPERLINK(\"x\")").StartsWith("\"'") && SelfData.CsvCell("@SUM(1)").StartsWith("\"'") && SelfData.CsvCell("a\"b\nc") == "\"a\"\"b\nc\"");
    await File.WriteAllTextAsync(path, "existing user export"); bool blocked = false;
    try { await CsvExporter.WriteAsync(path, ["a"], (p, t) => Task.FromResult(new Page<string>(p == 1 ? 11 : 12, Enumerable.Range(0, p == 1 ? 10 : 2).Select(i => (i + p * 10).ToString()).ToArray(), "")), r => [r], none); } catch (SelfException) { blocked = true; }
    Check("Changing totals abort export and preserve original file", blocked && await File.ReadAllTextAsync(path) == "existing user export" && !Directory.EnumerateFiles(root, "*.partial-*").Any());
    using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool canceled = false; try { await CsvExporter.WriteAsync(path, ["a"], (p, t) => Task.FromResult(new Page<string>(0, [], "")), r => [r], cancel.Token); } catch (OperationCanceledException) { canceled = true; }
    Check("Canceled export leaves no partial file and preserves existing data", canceled && await File.ReadAllTextAsync(path) == "existing user export" && !Directory.EnumerateFiles(root, "*.partial-*").Any());
    await CsvExporter.WriteAsync(path, ["value"], (p, t) => Task.FromResult(new Page<string>(11, Enumerable.Repeat("same visible value", p == 1 ? 10 : 1).ToArray(), "")), r => [r], none);
    Check("Legitimate records with identical visible values are all retained", File.ReadAllLines(path).Length == 12);
}
finally { foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file); Directory.Delete(root); }
if (args.Contains("--live-read"))
{
    if (Wifi.Read().Kind == "nufe")
    {
        using var live = new SelfClient { Consent = true }; using var timeout = new CancellationTokenSource(20000);
        var challenge = await live.PrepareLoginAsync(timeout.Token); Check("Live anonymous NUFE login form and captcha read without credentials", challenge.Captcha.Length > 0);
    }
    else Console.WriteLine("SKIP live anonymous read: NUFE-STU not positively identified");
}
Console.WriteLine($"Passed {checks.Count(c => c.Item2)}, failed {checks.Count(c => !c.Item2)}; synthetic HTTP transport + real export files; optional live step is anonymous read only"); return checks.All(c => c.Item2) ? 0 : 1;

sealed class Fixture : HttpMessageHandler
{
    public string Mode = ""; public bool Expired, IncrementUsage; public int Calls, LoginPosts, BindingPosts, Offlines; public bool SafeLogin, SafeBinding = true, HistoryQueries = true; private bool authenticated, bound = true, online = true;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++; string path = request.RequestUri!.AbsolutePath; if (request.RequestUri.Host != "10.100.0.3" || request.RequestUri.Port != 8080) throw new InvalidOperationException("Unexpected fixture destination"); string text;
        if (path == "/Self/login/") { authenticated = false; text = $"<form method='post' action='{(Mode == "foreign_login" ? "http://example.invalid/Self/login/verify" : "/Self/login/verify")}'><input name='account'><input name='{(Mode == "missing_password" ? "changed" : "password")}'><input name='foo' value='opaque&amp;field'><input name='checkcode' value='1'><input name='bar' value='second'><input name='code'></form><div class='hide' id='randomDiv'></div>" + (Mode == "md5_changed" ? "<script>var md5=true;</script>" : ""); }
        else if (path == "/Self/login/randomCode") text = "image";
        else if (path == "/Self/login/verify") { LoginPosts++; var fields = Fields(await request.Content!.ReadAsStringAsync(token)); SafeLogin = fields["account"] == "14000001" && fields["password"] == "synthetic p+&%=value" && fields["foo"] == "opaque&field" && fields["bar"] == "second" && fields["code"] == "AB12"; authenticated = Mode != "wrong_password"; return Redirect("/Self/dashboard/unfollowed"); }
        else if (!authenticated || Expired) return Redirect("/Self/login/");
        else if (path == "/Self/dashboard/getOnlineList") text = online ? JsonSerializer.Serialize(new[] { new { sessionId = "session-1", mac = Mode == "changed_session" ? "FFEEDDCCBBAA" : "AABBCCDDEEFF", ip = "192.0.2.2", loginTime = "2026-10-30 10:00:00", useTime = IncrementUsage ? 121 : 120, downFlow = 512, upFlow = 512, hostName = "Synthetic device", terminalType = "phone" } }) : "[]";
        else if (path == "/Self/dashboard/tooffline") { Offlines++; online = false; text = "{}"; }
        else if (path == "/Self/dashboard") text = "<script>var user={\"userName\":\"14000001\",\"serviceDefault\":\"Synthetic plan\",\"useFlag\":1,\"useTime\":5,\"useFlow\":3,\"leftMoney\":10,\"userPassword\":\"server-secret\"};</script>";
        else if (path == "/Self/bill/getUserOnlineLog")
        {
            if (Mode == "html_session") return Response("<form action='/Self/login/verify'><input name=\"password\"></form>"); if (Mode == "bad_json") return Response("not json"); var query = Fields(request.RequestUri.Query.TrimStart('?')); int page = int.Parse(query["pageNumber"]); HistoryQueries &= query["pageSize"] == "10" && query["startTime"] == "2026-10-01" && query["endTime"] == "2026-10-30" && query["sortName"] == "loginTime" && query["sortOrder"] == "DESC";
            int count = Mode == "too_many_rows" ? 11 : page == 1 ? 10 : 1; text = JsonSerializer.Serialize(new { total = 11, summary = new { TIME = 22, FLOW = 33, COSTMONEY = 0 }, rows = Enumerable.Range((page - 1) * 10, count).Select(i => new { loginTime = $"2026-10-{i + 1:00} 10:00:00", logoutTime = $"2026-10-{i + 1:00} 10:02:00", userIp = "192.0.2.2", macAddress = "AABBCCDDEEFF", time = 2, flow = 3, costMoney = 0, userPhone = "private-phone" }) });
        }
        else if (path == "/Self/bill/getMonthPay") text = "{\"total\":1,\"summary\":{},\"rows\":[[\"2026-10-01\",\"2026-10-31\",\"Synthetic plan\",10,0,2,3,\"2026-10-31\"]]}";
        else if (path == "/Self/service/operatorId")
        {
            text = $"<form method='post' action='{(Mode == "foreign_binding" ? "http://example.invalid/Self/service/bind-operator" : "/Self/service/bind-operator")}'><input name='csrftoken' value='{(Mode == "missing_csrf" ? "" : "opaque-csrf")}'>";
            for (int i = 1; i <= 6; i++) { string value = i switch { 1 when bound => Mode == "changed_account" ? "13800000001" : "13800000000", 2 when bound => "server-secret", 3 when Mode == "other_group" => "other-account", 4 when Mode is "other_group" or "password_only_other" => "server-secret", _ => "" }; text += $"<input name='FLDEXTRA{i}' value='{value}'>"; }
            text += "</form>";
        }
        else if (path == "/Self/service/bind-operator")
        {
            BindingPosts++; var fields = Fields(await request.Content!.ReadAsStringAsync(token)); bool clear = fields["FLDEXTRA1"].Length == 0; SafeBinding &= fields.Count == 7 && fields["csrftoken"] == "opaque-csrf" && fields["FLDEXTRA2"] == (clear ? "" : "local-broadband") && Enumerable.Range(3, 4).All(n => fields["FLDEXTRA" + n] == ""); bound = !clear; if (Mode == "lost_response") throw new HttpRequestException("Synthetic lost response"); return Redirect("http://example.invalid/unfollowed");
        }
        else throw new InvalidOperationException("Unexpected fixture route"); return Response(text);
    }
    private static Dictionary<string, string> Fields(string s) => s.Split('&').Where(p => p.Length > 0).Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
    private static HttpResponseMessage Response(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Redirect(string s) => new(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri(s, UriKind.RelativeOrAbsolute) } };
}
