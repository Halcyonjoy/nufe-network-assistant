using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private readonly DispatcherTimer historyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool readingHistory;
    private sealed record KeepAliveRow(KeepAliveRun Run)
    {
        public string Id => Run.Id;
        public string Time => Run.StartedAt.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss");
        public string Source => Run.Source == KeepAliveSource.Scheduler ? "计划任务" : Run.NetworkEvent ? "后台 · 启动/网络变化" : "后台 · 定时检查";
        public string Observation => Run.Steps.Contains("authentication_online") ? "检查时已认证" : Run.Steps.Contains("authentication_offline") ? "检查时未认证" : Run.Steps.Contains("forwarded_to_agent") ? "由常驻后台处理" : "未确认认证状态";
        public string Result => Run.Phase switch
        {
            "online" => Run.AuthenticationAttempted ? "认证成功" : Run.Steps.Contains("forwarded_to_agent") ? "后台处理完成 · 已认证" : "已在线，无需认证",
            "off_campus" => "已跳过 · 非校园网",
            "paused" => "已跳过 · 保活暂停",
            "disabled" => "已跳过 · 保活关闭",
            "busy" => "已跳过 · 正在处理其他操作",
            "cancelled" => "本次已中止",
            "offline" => "离线，等待重试",
            "unreachable" or "timeout" or "unconfirmed" => "结果未确认",
            _ => Run.AuthenticationAttempted ? "认证未成功" : "需要处理"
        };
        public string Duration => Math.Max(0, (Run.FinishedAt - Run.StartedAt).TotalSeconds).ToString("0.##") + " 秒";
    }
    private void InitializeKeepAliveHistory()
    {
        historyTimer.Tick += async (_, _) => await RefreshKeepAliveHistory();
        StateChanged += (_, _) => UpdateHistoryPolling();
        Closed += (_, _) => historyTimer.Stop();
    }
    private void UpdateHistoryPolling()
    {
        if (IsLoaded && !removed && WindowState != WindowState.Minimized && MainTabs.SelectedItem == KeepAliveHistoryTab)
        { historyTimer.Start(); _ = RefreshKeepAliveHistory(); }
        else historyTimer.Stop();
    }
    private async Task RefreshKeepAliveHistory()
    {
        if (readingHistory || closed.IsCancellationRequested || removed || WindowState == WindowState.Minimized || MainTabs.SelectedItem != KeepAliveHistoryTab) return;
        readingHistory = true;
        try
        {
            var records = await Task.Run(() => store.ReadKeepAliveHistory(), closed.Token);
            if (closed.IsCancellationRequested) return;
            string? selected = (KeepAliveHistoryGrid.SelectedItem as KeepAliveRow)?.Id;
            // Avoid rebuilding the DataGrid every five seconds when no execution has completed.
            var previous = (KeepAliveHistoryGrid.ItemsSource as KeepAliveRow[]) ?? [];
            if (!previous.Select(r => r.Id).SequenceEqual(records.Select(r => r.Id)))
            {
                var rows = records.Select(r => new KeepAliveRow(r)).ToArray();
                KeepAliveHistoryGrid.ItemsSource = rows;
                KeepAliveHistoryGrid.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
            }
            KeepAliveHistoryNote.Text = records.Length == 0
                ? "暂无记录。从此版本开始记录后台执行；开启保活并等待一次检查后刷新。前台状态查询不生成保活记录。"
                : $"最近 {records.Length} 次执行 · 更新于 {DateTimeOffset.Now:HH:mm:ss} · 此页每 5 秒刷新，最小化暂停。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { KeepAliveHistoryNote.Text = "保活记录暂时无法读取，请稍后刷新。已有列表保留。"; }
        finally { readingHistory = false; }
    }
    private async void RefreshHistoryClick(object sender, RoutedEventArgs e) => await RefreshKeepAliveHistory();
    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeepAliveHistoryGrid.SelectedItem is not KeepAliveRow row) { KeepAliveHistoryDetails.Text = "选择一条记录查看执行过程。"; return; }
        string reason = row.Run.Issue switch
        {
            "process_failed" => "子工具未正常完成，请结合诊断日志检查。",
            "configuration_error" => "读取配置或执行时出现异常。",
            "strategy_changed" => "保活策略已切换，本次未执行认证。",
            "stopped" => "保活已停止，本次未执行认证。",
            _ => Texts.Issue(row.Run.Issue)
        };
        KeepAliveHistoryDetails.Text = $"{row.Run.StartedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss} · {row.Source}\n" +
            string.Join("\n", row.Run.Steps.Select((step, index) => $"{index + 1}. {HistoryStep(step)}")) +
            $"\n结果：{row.Result} · 耗时 {row.Duration}" + (reason.Length == 0 ? "" : "\n说明：" + reason);
    }
    private static string HistoryStep(string step) => step switch
    {
        "started" => "开始本次保活检查",
        "process_started" => "启动认证子工具",
        "process_completed" => "子工具执行结束",
        "network_confirmed" => "确认连接 NUFE-STU 校园网",
        "checking_authentication" => "查询本机认证状态",
        "authentication_online" => "发现本机已处于认证状态",
        "authentication_offline" => "发现本机尚未认证",
        "authentication_requested" => "尝试提交校园网认证",
        "authentication_confirmed" => "认证成功，已确认联网认证状态",
        "already_online" => "保留现有认证，无需重新认证",
        "quota_recovery" => "遇到设备数超限，检查白名单处理规则",
        "quota_recovery_completed" => "已按规则处理设备名额，准备再次认证",
        "quota_recovery_failed" => "设备名额处理未完成，停止本次自动处理",
        "paused" => "保活已暂停，不提交认证",
        "disabled" => "保活未启用，不提交认证",
        "off_campus" => "当前不在校园网，跳过认证",
        "wifi_permission" => "无法确认 Wi-Fi 状态，跳过认证",
        "no_credentials" => "没有可用的已保存凭据",
        "consent_required" => "尚未确认校方入口传输说明",
        "busy" => "其他认证或配置操作占用执行入口，本次跳过",
        "cancelled" => "本次检查被停止或手动操作中断",
        "timeout" => "执行超时，结果未确认",
        "network_error" => "网络或服务器暂不可用",
        "needs_attention" => "遇到需要处理的情况，未自动重复提交",
        "retry_backoff" => "按重试退避规则等待下一次检查",
        "strategy_changed" => "当前已切换到其他保活策略",
        "forwarded_to_agent" => "发现常驻后台，将请求交由后台处理",
        _ => "执行遇到异常，结果未确认"
    };
    private async void LogoutOnceClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("注销本机认证", "仅注销当前配置账号在本机的校园网认证，不暂停或关闭保活。保活已启用时，可能很快自动重新认证。其他账号在线时不会注销。确认继续？")) return;
        await Run(async () =>
        {
            AgentResponse result;
            var running = await AgentForConfiguration();
            if (running != null && sessionCredential == null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                result = await AgentPipe.CallAsync(pipe, "logout_once", timeout.Token);
            }
            else
            {
                if (running != null) throw new UserNotice("后台仍在运行，请先核对正在使用的账号。");
                var session = sessionCredential; string carrier = sessionCarrier; bool consent = HttpConsent.IsChecked == true;
                result = await Task.Run(async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(65));
                    using var lease = await RunnerLease.AcquireAsync(store, timeout.Token);
                    using var portal = new CampusPortal();
                    return await OneShotLogout.RunAsync(store, portal, testRoot == null ? Wifi.Read : () => new("off_campus"), timeout.Token,
                        session, session == null ? null : carrier, consent);
                }, closed.Token);
            }
            Feedback.Text = result.Ok ? result.Code == "already_offline" ? "本机当前未认证，无需注销。保活设置保持原状态。" : "本机认证已注销。保活设置保持原状态，已启用的保活可能自动重新认证。"
                : result.Code == "logout_unconfirmed" ? "注销结果未确认，未自动重发。请刷新认证状态；保活设置保持原状态。" : Texts.Issue(result.Code);
            await RefreshStatus();
        });
    }
}
