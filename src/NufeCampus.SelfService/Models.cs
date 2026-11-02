using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace NufeCampus.SelfService;

public sealed class SelfException(string code) : Exception { public string Code { get; } = code; }
// Diagnostics contain counts only; never retain server rows or account identifiers in errors.
public sealed class PageReadException(string code, int page, int received, int total) : Exception
{
    public string Code { get; } = code;
    public int Page { get; } = page;
    public int Received { get; } = received;
    public int Total { get; } = total;
    public string Display => $"{SelfData.Message(Code)}（第 {Page} 页，已读取 {Received}/{Total} 条）。未将不完整结果显示为全部记录。";
}
public sealed record LoginChallenge(bool CaptchaRequired, byte[] Captcha);
public sealed record OnlineDevice(string SessionId, string Mac, string Ip, string LoginTime, string Minutes, string FlowMb, string Host, string Type, string UpMb = "—", string DownMb = "—")
{
    // WPF uses ToString for a row's accessible name. Do not expose the internal operation identifier.
    public override string ToString() => Host + " · " + Mac + " · " + LoginTime;
}
public sealed record HistoryRow(string LoginTime, string LogoutTime, string Ip, string Mac, string Minutes, string FlowMb, string Cost, string InternetUp = "", string InternetDown = "", string ChinaUp = "", string ChinaDown = "", string NasIp = "", string NasPort = "");
public sealed record BusinessRow(string Time, string Description, string Terminal, string Memo, string ScheduledDate = "");
public enum ServiceAction { StopNow, ScheduleStop, CancelStop, ReopenNow, ScheduleReopen, CancelReopen }
public sealed record ServiceState(bool Reopen, string Description, IReadOnlyList<ServiceAction> Allowed) { public string Fingerprint => Description + "|" + string.Join(',', Allowed); }
public sealed record ServiceTicket(ServiceAction Action, DateOnly? Date, ServiceState Before, DateTimeOffset Expires);
public sealed record PlatformArticle(string Title, string Text, string Source);
public sealed record BillRow(string Start, string End, string Plan, string BaseCost, string ExtraCost, string Minutes, string FlowMb, string Created);
public sealed record AccountOverview(string Account, string State, string Plan, string Minutes, string Flow, string Balance);
public sealed record Page<T>(int Total, IReadOnlyList<T> Rows, string Summary, string SourceFingerprint = "");
public sealed record OperatorGroup(string Name, string Account, bool HasPassword) { public bool Active => Account.Length != 0 || HasPassword; public string Display => !Active ? "未绑定" : Account.Length > 4 ? "***" + Account[^4..] : "已隐藏"; }
public sealed record OperatorState(IReadOnlyList<OperatorGroup> Groups) { public bool MobileWritable => CanWrite(0); public bool CanWrite(int group) => group is >= 0 and < 3 && !Groups.Where((_, i) => i != group).Any(g => g.Active); public string Fingerprint => string.Join("|", Groups.Select(g => g.Account.Length + ":" + g.Account + ":" + g.HasPassword)); }
public sealed record OfflineTicket(OnlineDevice Device, DateTimeOffset Expires);
public sealed record BindingTicket(OperatorState Before, bool Unbind, string Account, DateTimeOffset Expires, int Group = 0);
public sealed record OperatorFormTicket(OperatorState Before, string InputFingerprint, DateTimeOffset Expires);
public sealed record BoundDevice(string State, string Mac, string Terminal, string LastLogin, string Ip);
public sealed record MacTicket(BoundDevice Device, DateTimeOffset Expires);
public sealed record PlatformFeature(string Name, string Url, bool Builtin = false)
{
    public override string ToString() => Name + (Builtin ? " · 工具内查看" : " · 官网处理");
}

public static class SelfData
{
    public static string Value(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? Scalar(value) : "";
    public static string Scalar(JsonElement value) => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? Limit(value.ToString()) : "";
    public static string Limit(string value) => value.Length <= 512 ? value : value[..512];
    public static string Date(string value)
    {
        if (long.TryParse(value, out long milliseconds) && milliseconds > 100_000_000_000 && milliseconds < 253402185600000)
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss");
        return value;
    }
    public static string Decimal(string value, decimal divisor = 1) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (n / divisor).ToString("0.##", CultureInfo.InvariantCulture) : "—";
    public static void Range(DateOnly start, DateOnly end) { if (end < start || end.DayNumber - start.DayNumber > 59) throw new SelfException("range"); }
    public static string Message(string code) => code switch
    {
        "off_campus" => "当前未确认连接 NUFE-STU，未发送请求。",
        "consent" => "请先确认自助平台 HTTP 传输说明。",
        "session" => "自助会话尚未登录或已失效，请手动登录一次；保活不受影响。",
        "login" => "本次自助登录未确认成功，未自动重试。请核对密码或验证码。",
        "captcha" => "平台要求验证码，请刷新登录准备、填写图片中的验证码后再登录。",
        "protocol" => "平台页面或响应格式发生变化，已停止操作，可使用官方网页核对。",
        "range" => "请选择先后正确、包含首尾最多 60 天的日期。",
        "page" => "页码或年份无效。",
        "changed" => "目标或绑定状态已变化，未提交。请刷新后重新确认。",
        "page_repeat" => "平台重复返回已读取的分页",
        "page_total" => "读取期间平台记录总数发生变化，请刷新重试",
        "page_gap" => "平台分页返回的记录条数不完整",
        "history_mismatch" => "官网完整导出与查询记录不一致，未标记为完整；请刷新或缩小范围后核对。",
        "expired" => "确认已过期或已使用，请重新预览操作。",
        "other_groups" => "存在其他非空运营商组；尚无法证明完整表单会保留其密码，已保护其他组并停止提交。",
        "broadband" => "请填写所选运营商的宽带账号和密码；解绑也需掌握恢复资料。",
        "uncertain" => "请求可能已提交，但结果未确认。不会自动重发，请先只读刷新核对。",
        "verification" => "页面状态已更新；宽带密码是否正确仍需实际联网确认，不会自动下线测试。",
        "too_many" => "筛选结果过多，请缩小日期范围后导出。",
        "account" => "登录账号与预期不一致，未继续操作。",
        _ => "操作未完成。请检查校园连接或使用官方网页；不会自动重发写操作。"
    };
    public static string CsvCell(string text)
    {
        if (text.TrimStart().StartsWith('=') || Regex.IsMatch(text.TrimStart(), @"^[-+@]") || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    public static string Csv(IEnumerable<string> cells) => string.Join(",", cells.Select(CsvCell));
}
