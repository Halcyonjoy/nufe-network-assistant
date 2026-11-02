using AngleSharp.Html.Parser;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace NufeCampus.SelfService;

public sealed partial class SelfClient
{
    public Task<byte[]> ExportHistoryAsync(DateOnly start, DateOnly end, CancellationToken token) => Locked(async () =>
    {
        RequireSession(); SelfData.Range(start, end); Guard();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, $"bill/exportUserOnlineLog?type=2&startTime={start:yyyy-MM-dd}&endTime={end:yyyy-MM-dd}"));
        request.Headers.Referrer = new(Base, "bill/userOnlineLog");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is >= 300 and < 400) { Authenticated = false; tickets.Clear(); throw new SelfException("session"); }
        response.EnsureSuccessStatusCode(); using var input = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); byte[] chunk = new byte[8192]; int read;
        while ((read = await input.ReadAsync(chunk, token)) > 0) { if (buffer.Length + read > 8_000_000) throw new SelfException("too_many"); buffer.Write(chunk, 0, read); }
        byte[] result = buffer.ToArray(); if (result.Length < 8 || !result.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) throw new SelfException("protocol");
        return result;
    }, token);
    // Read-only diagnostic: emit only source-level pagination mappings, never HTML or script payloads.
    public Task<string> HistoryContractAsync(CancellationToken token) => Locked(async () =>
    {
        RequireSession(); using var doc = new HtmlParser().ParseDocument(await Get("bill/userOnlineLog", token));
        var lines = new HashSet<string>();
        foreach (var script in doc.QuerySelectorAll("script:not([src])"))
        {
            string text = script.TextContent; if (text.Contains("window.user", StringComparison.Ordinal) || (!text.Contains("queryParams", StringComparison.Ordinal) && !text.Contains("getUserOnlineLog", StringComparison.Ordinal) && !text.Contains("exportUserOnlineLog", StringComparison.Ordinal))) continue;
            foreach (Match m in Regex.Matches(text, "['\"]?(?<key>[A-Za-z][A-Za-z0-9_]*)['\"]?\\s*:\\s*(?<value>[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z][A-Za-z0-9_]*(?:\\s*[-+]\\s*[0-9]+)?|[0-9]+)\\b"))
            {
                string key = m.Groups["key"].Value, value = m.Groups["value"].Value;
                if (Regex.IsMatch(value, @"\.(pageNumber|pageSize|offset|limit)\b") || key is "pageSize" or "pageNumber" or "pageIndex" or "offset" or "limit") lines.Add(key + "=" + value);
            }
            foreach (Match m in Regex.Matches(text, "(?<key>queryParamsType|method)\\s*:\\s*['\"](?<value>limit|get|post|GET|POST|)['\"]")) lines.Add(m.Groups["key"].Value + "=" + m.Groups["value"].Value);
        }
        return lines.Count == 0 ? "官网页面未识别到分页配置；需重新核对接口。" : string.Join(Environment.NewLine, lines.Order(StringComparer.Ordinal));
    }, token);
    // Read every server page, preserving identical-looking legitimate sessions.
    public static async Task<Page<T>> ReadAllAsync<T>(Func<int, CancellationToken, Task<Page<T>>> query, CancellationToken token)
    {
        var first = await query(1, token); if (first.Total > 10_000) throw new SelfException("too_many");
        var result = new List<T>(first.Rows); int expected = first.Total; var seen = new HashSet<string>(); if (first.SourceFingerprint.Length > 0) seen.Add(first.SourceFingerprint);
        if (first.Rows.Count != Math.Min(10, expected)) throw new PageReadException("page_gap", 1, result.Count, expected);
        for (int page = 2; result.Count < expected; page++)
        {
            token.ThrowIfCancellationRequested();
            var next = await query(page, token);
            if (next.Total != expected) throw new PageReadException("page_total", page, result.Count, expected);
            if (next.SourceFingerprint.Length > 0 && !seen.Add(next.SourceFingerprint)) throw new PageReadException("page_repeat", page, result.Count, expected);
            if (next.Rows.Count != Math.Min(10, expected - result.Count)) throw new PageReadException("page_gap", page, result.Count, expected);
            result.AddRange(next.Rows);
        }
        return new(expected, result, first.Summary);
    }
    public async Task<Page<HistoryRow>> HistoryRangeAsync(DateOnly start, DateOnly end, CancellationToken token)
    {
        if (end < start || end.DayNumber - start.DayNumber > 3660) throw new SelfException("range");
        var rows = new List<HistoryRow>(); var summaries = new List<string>();
        for (var from = start; from <= end;)
        {
            var to = DateOnly.FromDayNumber(Math.Min(end.DayNumber, from.DayNumber + 59));
            var block = await ReadAllAsync((p, t) => HistoryAsync(from, to, p, t), token);
            if (rows.Count + block.Total > 10_000) throw new SelfException("too_many"); rows.AddRange(block.Rows);
            summaries.Add($"{from:yyyy-MM-dd}～{to:yyyy-MM-dd}：{block.Summary}"); if (to == end) break; from = to.AddDays(1);
        }
        return new(rows.Count, rows.OrderByDescending(r => r.LoginTime, StringComparer.Ordinal).ThenByDescending(r => r.LogoutTime, StringComparer.Ordinal).ToArray(), string.Join(Environment.NewLine, summaries));
    }
    public Task<Page<BusinessRow>> BusinessAsync(DateOnly start, DateOnly end, int page, CancellationToken token) => Locked(async () =>
    {
        RequireSession(); SelfData.Range(start, end); if (page < 1 || page > 1000) throw new SelfException("page");
        using var json = JsonDocument.Parse(await Get($"bill/getOperatorLog?pageNumber={page}&pageSize=10&startTime={start:yyyy-MM-dd}&endTime={end:yyyy-MM-dd}&sortName=0&sortOrder=DESC", token)); var root = json.RootElement;
        return new Page<BusinessRow>(Total(root), Rows(root).EnumerateArray().Select(r =>
        {
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 5) throw new SelfException("protocol");
            return new BusinessRow(SelfData.Date(SelfData.Scalar(r[0])), SelfData.Scalar(r[1]), SelfData.Scalar(r[2]), SelfData.Scalar(r[4]));
        }).ToArray(), "");
    }, token);
    public Task<Page<BusinessRow>> ServiceLogAsync(bool reopen, int page, CancellationToken token) => Locked(async () =>
    {
        RequireSession(); if (page < 1 || page > 1000) throw new SelfException("page");
        using var json = JsonDocument.Parse(await Get($"service/{(reopen ? "goReopenLog" : "getStopLog")}?pageNumber={page}&pageSize=10&sortOrder=DESC", token)); var root = json.RootElement;
        return new Page<BusinessRow>(Total(root), Rows(root).EnumerateArray().Select(r => new BusinessRow(SelfData.Date(SelfData.Value(r, "fldoperatedate")), SelfData.Value(r, "fldoperateid") switch { "8" => "立即报停", "9" => "立即复通", _ => reopen ? "预约复通" : "预约报停" }, SelfData.Value(r, "fldadminid"), SelfData.Value(r, "fldmemo"), SelfData.Value(r, "fldnewvalue"))).ToArray(), "");
    }, token);
    private sealed record ServiceForm(ServiceState State, string Token);
    private async Task<ServiceForm> ReadService(bool reopen, CancellationToken token)
    {
        RequireSession(); using var doc = new HtmlParser().ParseDocument(await Get(reopen ? "service/goReopen" : "service/goStop", token));
        var form = doc.QuerySelector("form") ?? throw new SelfException("protocol");
        // Never parse or retain the server's window.user payload (it may contain credentials).
        var actions = new List<ServiceAction>();
        void Add(string selector, ServiceAction action) { var e = doc.QuerySelector(selector); if (e != null && !e.HasAttribute("disabled") && !e.ClassList.Contains("disabled")) actions.Add(action); }
        if (reopen) { Add("#reOpenNow", ServiceAction.ReopenNow); Add("#reOpen", ServiceAction.ScheduleReopen); Add("[data-toggle='undoPreReopen']", ServiceAction.CancelReopen); }
        else { Add("#stopNow", ServiceAction.StopNow); Add("#stop", ServiceAction.ScheduleStop); Add("[data-toggle='undoPreStop']", ServiceAction.CancelStop); }
        var script = doc.QuerySelectorAll("script:not([src])").FirstOrDefault(e => e.TextContent.Contains(reopen ? "function userOpen(" : "function disable("))?.TextContent ?? "";
        var m = Regex.Match(script, "var\\s+AJAXCSRFTOKEN\\s*=\\s*['\"](?<v>[^'\"\\r\\n]{1,200})['\"]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!m.Success) throw new SelfException("protocol");
        string text = string.Join(Environment.NewLine, form.TextContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (text.Length > 10_000) throw new SelfException("protocol"); return new(new(reopen, text, actions), m.Groups["v"].Value);
    }
    public Task<ServiceState> ServiceStateAsync(bool reopen, CancellationToken token) => Locked(async () => (await ReadService(reopen, token)).State, token);
    public Task<ServiceTicket> PrepareServiceAsync(ServiceAction action, DateOnly? date, CancellationToken token) => Locked(async () =>
    {
        bool reopen = action is ServiceAction.ReopenNow or ServiceAction.ScheduleReopen or ServiceAction.CancelReopen;
        if (action == ServiceAction.ScheduleReopen && (date == null || date <= DateOnly.FromDateTime(clock().ToOffset(TimeSpan.FromHours(8)).Date))) throw new SelfException("range");
        var form = await ReadService(reopen, token); if (!form.State.Allowed.Contains(action)) throw new SelfException("changed");
        var ticket = new ServiceTicket(action, date, form.State, clock().AddSeconds(45)); tickets.Clear(); tickets.Add(ticket); return ticket;
    }, token);
    public Task<bool> SubmitServiceAsync(ServiceTicket ticket, CancellationToken token) => Locked(async () =>
    {
        Consume(ticket, ticket.Expires); var form = await ReadService(ticket.Before.Reopen, token);
        if (form.State.Fingerprint != ticket.Before.Fingerprint || !form.State.Allowed.Contains(ticket.Action)) throw new SelfException("changed");
        string endpoint = ticket.Action switch { ServiceAction.CancelStop => "undoStop", ServiceAction.CancelReopen => "undoReOpen", ServiceAction.ReopenNow or ServiceAction.ScheduleReopen => "reOpen", _ => "stop" };
        var fields = new Dictionary<string, string> { { "ajaxCsrfToken", form.Token } };
        if (ticket.Action is not (ServiceAction.CancelStop or ServiceAction.CancelReopen))
        {
            fields["flag"] = ticket.Action is ServiceAction.StopNow or ServiceAction.ReopenNow ? "1" : "2";
            fields["t"] = clock().ToUnixTimeMilliseconds().ToString();
            if (ticket.Action == ServiceAction.ScheduleReopen) fields["date"] = ticket.Date!.Value.ToString("yyyy-MM-dd");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, "service/" + endpoint)) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Referrer = new(Base, ticket.Before.Reopen ? "service/goReopen" : "service/goStop"); request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Guard(); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new SelfException("uncertain");
        using var json = JsonDocument.Parse(await Read(response, token)); if (SelfData.Value(json.RootElement, "state") != "success") throw new SelfException("business");
        // An unchanged page cannot prove that the requested state transition took effect.
        var after = await ReadService(ticket.Before.Reopen, token); if (after.State.Fingerprint == form.State.Fingerprint) throw new SelfException("uncertain"); return true;
    }, token);
    public Task<PlatformArticle[]> ArticlesAsync(bool agreement, CancellationToken token) => Locked(async () =>
    {
        string path = agreement ? "unlogin/agreement" : "unlogin/help";
        using var doc = new HtmlParser().ParseDocument(await Get(path, token));
        static string Text(AngleSharp.Dom.IElement e) { foreach (var n in e.QuerySelectorAll("script,style,form,button,a[href^='javascript:']")) n.Remove(); return Regex.Replace(e.TextContent, @"[ \t]+", " ").Trim(); }
        if (agreement) { var content = doc.QuerySelectorAll(".container").LastOrDefault() ?? doc.Body ?? throw new SelfException("protocol"); return new[] { new PlatformArticle("服务协议（仅供阅读）", Text(content), new Uri(Base, path).AbsoluteUri) }; }
        var links = doc.QuerySelectorAll("a[href]").Select(e => (Title: e.TextContent.Trim(), Href: e.GetAttribute("href")!)).Where(x => Regex.IsMatch(x.Href, @"^/Self/unlogin/helpinfo/\d+$")).Distinct().Take(50).ToArray();
        if (links.Length == 0) throw new SelfException("protocol"); var articles = new List<PlatformArticle>();
        foreach (var link in links) { using var body = new HtmlParser().ParseDocument(await Get(link.Href, token)); var content = body.QuerySelectorAll(".container").LastOrDefault() ?? body.Body ?? throw new SelfException("protocol"); articles.Add(new(link.Title, Text(content), new Uri(Base, link.Href).AbsoluteUri)); }
        return articles.ToArray();
    }, token);
}
