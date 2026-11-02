using ExcelDataReader;
using System.Text;
using NufeCampus.App;
using NufeCampus.SelfService;
using System.Net;
using System.Text.Json;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length == 2 && args[0] == "--verify-file")
{
    try
    {
        byte[] bytes = File.ReadAllBytes(args[1]); var rows = OfficialHistory.Parse(bytes); Console.WriteLine($"Official workbook parsed: {rows.Length} records, 12 verified columns; no cell data logged.");
        int passed = 0, failed = 0; void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) passed++; else failed++; }
        foreach (string mode in new[] { "ok", "total_change", "truncated", "different_row", "stale_total", "redirect", "invalid_file" })
        {
            using var wire = new ExportWire(bytes, rows, mode); using var c = new SelfClient(wire, () => new("nufe", "192.0.2.1", "001122334455")) { Consent = true };
            await c.PrepareLoginAsync(default); await c.LoginAsync(new("14000001", "synthetic-only"), "", default);
            bool rejected = false; Page<HistoryRow>? result = null;
            try { result = await OfficialHistory.LoadAsync(c, new(2026, 10, 30), new(2026, 10, 30), default); } catch (SelfException) { rejected = true; }
            Check("Independent complete-export boundary " + mode, mode is "ok" or "stale_total" ? result?.Total == rows.Length && wire.Safe : rejected && wire.Safe);
        }
        var sample = new HistoryRow("2026-10-30 01:00:00", "2026-10-30 01:01:00", "192.0.2.1", "", "1", "0", "0", "0", "0", "0", "0", "192.0.2.2", "1");
        Check("Visible duplicates retain their multiplicity", OfficialHistory.Contains([sample, sample], [sample, sample]) && !OfficialHistory.Contains([sample], [sample, sample]));
        Check("A differing shared field cannot be marked equal", !OfficialHistory.Contains([sample], [sample with { NasPort = "2" }]));
        Check("Equivalent numeric formatting compares by value", OfficialHistory.Contains([sample], [sample with { Minutes = "1.0" }]));
        Console.WriteLine($"Passed {passed}, failed {failed}; local official workbook + isolated HTTP fixture, no school requests or agent."); return failed == 0 ? 0 : 1;
    }
    catch { Console.WriteLine("Official workbook parse rejected; no cell data logged."); return 1; }
}
if (args.Length == 2 && args[0] == "--inspect-headers")
{
    using var stream = File.OpenRead(args[1]); using var reader = ExcelReaderFactory.CreateReader(stream);
    int row = 0;
    while (reader.Read())
    {
        if (row++ > 8) break;
        var fields = Enumerable.Range(0, reader.FieldCount).Select(i => (i, Text: Convert.ToString(reader.GetValue(i)) ?? ""));
        var headers = fields.Where(x => row == 1 && Convert.ToString(reader.GetValue(0)) == "上线时间");
        Console.WriteLine($"Row {row}: " + string.Join("; ", headers.Select(x => $"{x.i}={x.Text}")));
    }
    return 0;
}
return 0;

sealed class ExportWire(byte[] bytes, HistoryRow[] rows, string mode) : HttpMessageHandler
{
    public bool Safe = true; private int historyCalls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken t)
    {
        string path = request.RequestUri!.AbsolutePath;
        HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
        if (path.EndsWith("/login/")) return Task.FromResult(Text("<form method='post' action='/Self/login/verify'><input name='account'><input name='password'></form>"));
        if (path.EndsWith("/randomCode")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        if (path.EndsWith("/verify")) return Task.FromResult(Text("ok"));
        if (path.EndsWith("getOnlineList")) return Task.FromResult(Text("[]"));
        Safe &= request.Method == HttpMethod.Get;
        if (path.EndsWith("getUserOnlineLog"))
        {
            historyCalls++; int total = rows.Length + (mode == "total_change" && historyCalls > 1 ? 1 : mode == "stale_total" ? 1 : 0);
            int page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["pageNumber"]!);
            var selected = rows.Skip((page - 1) * 10).Take(mode == "truncated" ? Math.Max(0, rows.Length - 1) : 10).Select(r => new { loginTime = r.LoginTime, logoutTime = r.LogoutTime, userIp = mode == "different_row" ? "192.0.2.99" : r.Ip, time = r.Minutes, flow = r.FlowMb, costMoney = r.Cost, internetUpFlow = r.InternetUp, internetDownFlow = r.InternetDown, chinanetUpFlow = r.ChinaUp, chinanetDownFlow = r.ChinaDown, nasIp = r.NasIp, nasPort = r.NasPort });
            return Task.FromResult(Text(JsonSerializer.Serialize(new { total, rows = selected })));
        }
        if (path.EndsWith("exportUserOnlineLog"))
        {
            Safe &= request.RequestUri.Query == "?type=2&startTime=2026-10-30&endTime=2026-10-30" && request.Headers.Referrer?.AbsolutePath == "/Self/bill/userOnlineLog";
            return Task.FromResult(new HttpResponseMessage(mode == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK) { Content = new ByteArrayContent(mode == "invalid_file" ? [1, 2, 3] : bytes) });
        }
        throw new InvalidOperationException("Unexpected isolated request");
    }
}
