using System.Windows;
using System.IO;
using NufeCampus.SelfService;
using NufeCampus.Windows;
namespace NufeCampus.App;

public partial class SelfPage
{
    private async void HistoryAuditClick(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        if (HistoryGrid.ItemsSource is not IEnumerable<HistoryRow> current || !loadedPages.Contains(1)) throw new SelfException("history_mismatch");
        var (start, end) = Dates(); var shown = current.ToArray(); var official = await CompleteHistory(start, end, token);
        bool equal = shown.Length == official.Total && OfficialHistory.Contains(official.Rows.ToArray(), shown);
        string report = $"官网完整记录核对\n{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n范围：{start:yyyy-MM-dd}～{end:yyyy-MM-dd}\n界面 {shown.Length} 条，官网完整导出 {official.Total} 条；逐条比较 12 个共享字段及重复次数：{(equal ? "全部一致" : "不一致，未验收")}。";
        string folder = Path.Combine(store.Root, "diagnostics"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "history-verification.txt"), report, token);
        SelfFeedback.Text = equal ? $"与官网全部 {official.Total} 条记录的 12 个字段逐条核对一致。" : SelfData.Message("history_mismatch");
    });
    private sealed record ServiceChoice(ServiceAction Action, string Label) { public override string ToString() => Label; }
    private void ClearPageData() { DevicesUpdated?.Invoke(null); DevicesGrid.ItemsSource = null; HistoryGrid.ItemsSource = null; BillsGrid.ItemsSource = null; BoundGrid.ItemsSource = null; BusinessGrid.ItemsSource = null; StopLogGrid.ItemsSource = null; ReopenLogGrid.ItemsSource = null; ServiceActionBox.ItemsSource = null; ServiceStateText.Text = "尚未加载"; OverviewText.Text = "尚未加载"; OperatorText.Text = "尚未加载"; PrefetchText.Text = "尚未预取"; HistorySummary.Text = HistoryInfo.Text = BillSummary.Text = BillInfo.Text = BoundInfo.Text = BusinessInfo.Text = ""; }
    private (DateOnly Start, DateOnly End) BusinessDates() { if (BusinessStart.SelectedDate is not { } start || BusinessEnd.SelectedDate is not { } end) throw new SelfException("range"); var a = DateOnly.FromDateTime(start); var b = DateOnly.FromDateTime(end); SelfData.Range(a, b); return (a, b); }
    private async Task Business(CancellationToken t) { var (a, b) = BusinessDates(); var all = await SelfClient.ReadAllAsync((p, c) => client.BusinessAsync(a, b, p, c), t); BusinessGrid.ItemsSource = all.Rows; BusinessInfo.Text = $"已加载全部 {all.Total} 条 · {a:yyyy-MM-dd} 至 {b:yyyy-MM-dd}"; }
    private void BusinessFilterChanged(object s, RoutedEventArgs e) { if (!IsLoaded) return; loadedPages.Remove(5); pageErrors.Remove(5); filterVersion++; filters.Stop(); filters.Start(); }
    private async void BusinessRefreshClick(object s, RoutedEventArgs e) => await Run(t => LoadPage(5, t));
    private async void BusinessExportClick(object s, RoutedEventArgs e) => await Run(async t => { var (a, b) = BusinessDates(); string? path = ExportPath("NUFE-业务办理记录.csv"); if (path == null) return; await Export(path, ["办理时间", "业务描述", "受理终端", "备注"], (p, c) => client.BusinessAsync(a, b, p, c), r => [r.Time, r.Description, r.Terminal, r.Memo], t); });
    private static string Label(ServiceAction a) => a switch { ServiceAction.StopNow => "立即报停", ServiceAction.ScheduleStop => "下周期预约报停", ServiceAction.CancelStop => "取消预约报停", ServiceAction.ReopenNow => "立即复通", ServiceAction.ScheduleReopen => "预约复通", _ => "取消预约复通" };
    private async Task AccountServices(CancellationToken t)
    {
        var stop = await client.ServiceStateAsync(false, t); var reopen = await client.ServiceStateAsync(true, t);
        ServiceStateText.Text = "报停页面：\n" + stop.Description + "\n\n复通页面：\n" + reopen.Description;
        ServiceActionBox.ItemsSource = stop.Allowed.Concat(reopen.Allowed).Select(a => new ServiceChoice(a, Label(a))).ToArray(); ServiceActionBox.SelectedIndex = 0;
        StopLogGrid.ItemsSource = (await SelfClient.ReadAllAsync((p, c) => client.ServiceLogAsync(false, p, c), t)).Rows;
        ReopenLogGrid.ItemsSource = (await SelfClient.ReadAllAsync((p, c) => client.ServiceLogAsync(true, p, c), t)).Rows;
        if (pendingWrite == "service") pendingWrite = "";
    }
    private void ServiceActionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e) { ReopenDate.Visibility = ServiceActionBox.SelectedItem is ServiceChoice { Action: ServiceAction.ScheduleReopen } ? Visibility.Visible : Visibility.Collapsed; }
    private async void ServiceRefreshClick(object s, RoutedEventArgs e) => await Run(t => LoadPage(6, t));
    private async void ServicePreviewClick(object s, RoutedEventArgs e) => await Run(async t =>
    {
        if (ServiceActionBox.SelectedItem is not ServiceChoice choice) { SelfFeedback.Text = "当前状态没有可办理操作，请刷新核对。"; return; }
        DateOnly? date = ReopenDate.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
        var ticket = await client.PrepareServiceAsync(choice.Action, date, t);
        string warning = choice.Action is ServiceAction.StopNow or ServiceAction.ScheduleStop ? "报停后将无法使用网络并停止计费；预约报停在本周期结束后生效。" : "复通成功后将恢复上网并开始计费，余额不足可能失败。取消预约会撤销原计划。";
        if (!await ConfirmAsync("账号业务最终确认", $"账号：{client.AuthenticatedAccount}\n操作：{choice.Label}" + (choice.Action == ServiceAction.ScheduleReopen ? $"\n日期：{date:yyyy-MM-dd}" : "") + "\n" + warning + "\n目的地：10.100.0.3:8080\n只提交一次；结果不明先刷新，不重发。确认提交？")) { SelfFeedback.Text = "已取消，未提交。"; return; }
        bool ownStop = choice.Action == ServiceAction.StopNow && store.ReadSecret()?.Account == client.AuthenticatedAccount;
        using var keepAliveLease = ownStop && store.ReadSettings().KeepAliveStrategy == "scheduler" ? await RunnerLease.AcquireAsync(store, t) : null;
        if (ownStop) store.UpdateSettings(x => x with { Memory = x.Memory with { PausedByTool = true } });
        pendingWrite = "service"; await client.SubmitServiceAsync(ticket, t); await LoadPage(6, t); loadedPages.Remove(2); loadedPages.Remove(5);
        SelfFeedback.Text = "服务器已接受，页面状态变化已核对；未运行保活或自动认证。立即报停后本机保活保持暂停，需手动恢复。";
    }, true);
}
