using NufeCampus.SelfService;
using NufeCampus.Windows;
using NufeCampus.App;
using System.Net;
using System.Text.Json;

int passed = 0, failed = 0; var none = CancellationToken.None;
void Check(string name, bool value) { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (value) passed++; else failed++; }
async Task<bool> Reject(Func<Task> work, string code) { try { await work(); return false; } catch (SelfException e) { return e.Code == code; } catch (PageReadException e) { return e.Code == code; } }
SelfClient Client(Wire wire, Func<DateTimeOffset>? clock = null) => new(wire, () => new("nufe", "192.0.2.1", "001122334455"), clock) { Consent = true };
async Task Login(SelfClient c) { await c.PrepareLoginAsync(none); await c.LoginAsync(new("14000001", "synthetic-only"), "", none); }
using (var wire = new Wire()) using (var c = Client(wire))
{
    await Login(c); var contract = await c.HistoryContractAsync(none);
    Check("Official pagination mappings can be audited without retaining unrelated script secrets", contract.Contains("pageNumber=params.pageNumber") && contract.Contains("pageSize=params.pageSize") && !contract.Contains("synthetic-secret") && !contract.Contains("private-account"));
    wire.QuotedContract = true; contract = await c.HistoryContractAsync(none);
    Check("Quoted official keys and different local parameter names retain pagination arithmetic", contract.Contains("pageNumber=q.pageNumber - 1") && contract.Contains("pageSize=q.pageSize") && !contract.Contains("synthetic-secret"));
    var rows = await c.HistoryRangeAsync(new(2026, 10, 2), new(2026, 10, 31), none);
    Check("History loads all 25 records across 3 actual adapter requests", rows.Total == 25 && rows.Rows.Count == 25 && wire.HistoryQueries.Count == 3);
    Check("History includes last-page record and all deployed flow/NAS fields", rows.Rows.Any(r => r.LoginTime.Contains("10-25")) && rows.Rows.All(r => r.InternetUp == "1" && r.InternetDown == "2" && r.ChinaUp == "3" && r.ChinaDown == "4" && r.NasIp == "192.0.2.254" && r.NasPort.Length > 0));
    wire.HistoryQueries.Clear(); var longRange = await c.HistoryRangeAsync(new(2026, 8, 1), new(2026, 9, 30), none);
    Check("61-day filter splits into disjoint bounded ranges without dropping pages", longRange.Total == 50 && wire.HistoryQueries.Count == 6 && wire.HistoryQueries[0].Contains("endTime=2026-09-29") && wire.HistoryQueries[3].Contains("startTime=2026-09-30"));
    var business = await c.BusinessAsync(new(2026, 10, 1), new(2026, 10, 31), 1, none);
    Check("Business array indices map date, description, terminal and memo only", business.Rows[0].Description == "示例修改资料" && business.Rows[0].Memo == "合成记录" && !JsonSerializer.Serialize(business).Contains("未展示内部字段"));
    var stop = await c.ServiceStateAsync(false, none); var reopen = await c.ServiceStateAsync(true, none);
    Check("Normal account offers stop actions but disabled reopen is never enabled", stop.Allowed.SequenceEqual([ServiceAction.StopNow, ServiceAction.ScheduleStop]) && reopen.Allowed.Count == 0);
    Check("Read-only state and log queries perform zero business writes", (await c.ServiceLogAsync(false, 1, none)).Total == 1 && (await c.ServiceLogAsync(true, 1, none)).Total == 1 && wire.Writes == 0);
    Check("Unavailable reopen is rejected before any write", await Reject(() => c.PrepareServiceAsync(ServiceAction.ReopenNow, null, none), "changed") && wire.Writes == 0);
    var help = await c.ArticlesAsync(false, none); var agreement = await c.ArticlesAsync(true, none);
    Check("Help follows only deployed same-origin helpinfo path", help.Single().Text == "暂无使用帮助信息" && help[0].Source.EndsWith("helpinfo/0"));
    Check("Agreement is plain text and script content is excluded", agreement[0].Text.Contains("这里展示读取的正文") && !agreement[0].Text.Contains("excluded-script-text"));
    var features = await c.FeaturesAsync(none); Check("Every known platform feature opens inside the tool", features.Length == 10 && features.All(f => f.Builtin));
    foreach (var action in new[] { ServiceAction.StopNow, ServiceAction.ScheduleReopen, ServiceAction.CancelReopen, ServiceAction.ReopenNow, ServiceAction.ScheduleStop, ServiceAction.CancelStop })
    {
        int before = wire.Writes; var ticket = await c.PrepareServiceAsync(action, new DateOnly(2099, 1, 1), none); await c.SubmitServiceAsync(ticket, none);
        Check("Exact service route, parameters and state readback: " + action, wire.Writes == before + 1 && wire.Safe);
        Check("Service confirmation cannot be replayed: " + action, await Reject(() => c.SubmitServiceAsync(ticket, none), "expired") && wire.Writes == before + 1);
    }
    Check("Past scheduled date never submits", await Reject(() => c.PrepareServiceAsync(ServiceAction.ScheduleReopen, new(2000, 1, 1), none), "range"));
}
using (var wire = new Wire()) using (var c = Client(wire)) { await Login(c); var ticket = await c.PrepareServiceAsync(ServiceAction.StopNow, null, none); wire.ChangeForm = true; Check("State changes after confirmation prevent final business POST", await Reject(() => c.SubmitServiceAsync(ticket, none), "changed") && wire.Writes == 0); }
{ var now = DateTimeOffset.UtcNow; using var wire = new Wire(); using var c = Client(wire, () => now); await Login(c); var ticket = await c.PrepareServiceAsync(ServiceAction.StopNow, null, none); now = now.AddSeconds(46); Check("Expired service confirmation never writes", await Reject(() => c.SubmitServiceAsync(ticket, none), "expired") && wire.Writes == 0); }
using (var wire = new Wire()) using (var c = Client(wire)) { await Login(c); var ticket = await c.PrepareServiceAsync(ServiceAction.StopNow, null, none); wire.LoseResponse = true; bool lost = false; try { await c.SubmitServiceAsync(ticket, none); } catch (HttpRequestException) { lost = true; } await c.ServiceStateAsync(false, none); Check("Lost POST response is read back without replay", lost && wire.Writes == 1 && await Reject(() => c.SubmitServiceAsync(ticket, none), "expired")); }
using (var wire = new Wire()) using (var c = Client(wire)) { await Login(c); var ticket = await c.PrepareServiceAsync(ServiceAction.StopNow, null, none); wire.FalseSuccess = true; Check("Success response with unchanged state remains uncertain", await Reject(() => c.SubmitServiceAsync(ticket, none), "uncertain") && wire.Writes == 1); }
var duplicate = await SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<string>(11, Enumerable.Repeat("same", p == 1 ? 10 : 1).ToArray(), "")), none); Check("Identical visible sessions are preserved", duplicate.Rows.Count == 11);
Check("Missing intermediate page is reported rather than silently truncated", await Reject(() => SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<int>(21, p == 1 ? Enumerable.Range(1, 10).ToArray() : [], "")), none), "page_gap"));
Check("Changing server total is reported rather than mixed", await Reject(() => SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<int>(p == 1 ? 11 : 12, Enumerable.Range(1, p == 1 ? 10 : 2).ToArray(), "")), none), "page_total"));
Check("Oversized result is explicit, not silently clipped", await Reject(() => SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<int>(10001, [], "")), none), "too_many"));
Check("Repeated server page is rejected rather than counted as new sessions", await Reject(() => SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<int>(20, Enumerable.Range(1, 10).ToArray(), "", "same-source-page")), none), "page_repeat"));
Check("Oversized final page is rejected before records can be accepted", await Reject(() => SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<int>(11, Enumerable.Range(1, 10).ToArray(), "")), none), "page_gap"));
Check("An incomplete first page is rejected even when later pages might fill the total", await Reject(() => SelfClient.ReadAllAsync((p, t) => Task.FromResult(new Page<int>(15, Enumerable.Range(1, p == 1 ? 5 : 10).ToArray(), "")), none), "page_gap"));
Check("Pagination diagnostics contain only reason and numeric progress", new PageReadException("page_repeat", 2, 10, 148).Display.Contains("第 2 页，已读取 10/148 条") && !new PageReadException("page_repeat", 2, 10, 148).Display.Contains("绑定"));
using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); bool canceled = false; try { await SelfClient.ReadAllAsync((p, t) => { t.ThrowIfCancellationRequested(); return Task.FromResult(new Page<int>(0, [], "")); }, cancel.Token); } catch (OperationCanceledException) { canceled = true; } Check("Canceled prefetch can stop before additional pages", canceled); }
Console.WriteLine($"Passed {passed}, failed {failed}; synthetic HTTP only, no agent or live business submission"); return failed == 0 ? 0 : 1;

sealed class Wire : HttpMessageHandler
{
    private readonly HttpMessageInvoker inner = new(new SelfDemoHandler()); public int Writes; public bool Safe = true, ChangeForm, LoseResponse, FalseSuccess, QuotedContract; public List<string> HistoryQueries = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken t)
    {
        string path = request.RequestUri!.AbsolutePath; if (path.EndsWith("getUserOnlineLog")) HistoryQueries.Add(request.RequestUri.Query);
        if (path == "/Self/bill/userOnlineLog") return new(HttpStatusCode.OK) { Content = new StringContent(QuotedContract ? "<script>var url='getUserOnlineLog';var data={'pageNumber':q.pageNumber - 1,'pageSize':q.pageSize,password:'synthetic-secret'};</script>" : "<script>window.user={password:'synthetic-secret',account:'private-account'};</script><script>var url='getUserOnlineLog'; var options={queryParamsType:'', queryParams:function(params){return {pageNumber:params.pageNumber,pageSize:params.pageSize,startTime:$('#date').val()};}};</script>") };
        bool write = path is "/Self/service/stop" or "/Self/service/reOpen" or "/Self/service/undoStop" or "/Self/service/undoReOpen";
        if (write) { Writes++; var values = (await request.Content!.ReadAsStringAsync(t)).Split('&').Select(s => s.Split('=', 2)).ToDictionary(s => WebUtility.UrlDecode(s[0]), s => WebUtility.UrlDecode(s[1])); Safe &= request.Method == HttpMethod.Post && values["ajaxCsrfToken"] == "synthetic-token" && request.Headers.Referrer?.AbsolutePath is "/Self/service/goStop" or "/Self/service/goReopen"; if (path.EndsWith("reOpen") && values.GetValueOrDefault("flag") == "2") Safe &= values["date"] == "2099-01-01"; if (FalseSuccess) return new(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"success\"}") }; }
        var response = await inner.SendAsync(request, t);
        if (ChangeForm && path.EndsWith("goStop")) { var text = await response.Content.ReadAsStringAsync(t); response.Content = new StringContent(text.Replace("合成套餐", "changed-plan")); }
        if (write && LoseResponse) throw new HttpRequestException("synthetic lost response"); return response;
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
