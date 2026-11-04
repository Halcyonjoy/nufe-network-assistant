using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NufeCampus.Windows;
using NufeCampus.SelfService;
namespace NufeCampus.App;

public partial class MainWindow
{
    private readonly NetworkMeter meter = new();
    private readonly DispatcherTimer rateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? speedCancellation;
    private bool measuring, checkingInternet, sampling;
    private NetworkConnection? displayedNetwork;
    private AgentStatus? latestStatus;
    private OnlineDevice? currentSession;
    private string sessionAccount = "";
    private DateTimeOffset sessionUpdated;
    private void InitializeFeatures()
    {
        rateTimer.Tick += async (_, _) => await SampleNetworkAsync();
        Loaded += async (_, _) => { if (WindowState != WindowState.Minimized) { rateTimer.Start(); await SampleNetworkAsync(); } };
        StateChanged += async (_, _) =>
        {
            if (!IsLoaded || closed.IsCancellationRequested) return;
            if (WindowState == WindowState.Minimized) { rateTimer.Stop(); refresh.Stop(); meter.Reset(); rateHistory.Clear(); }
            else { nextForegroundProbe = default; rateTimer.Start(); refresh.Start(); await SampleNetworkAsync(); await RefreshStatus(); }
        };
        Loaded += (_, _) => { var area = SystemParameters.WorkArea; MinHeight = Math.Min(MinHeight, area.Height - 24); MinWidth = Math.Min(MinWidth, area.Width - 24); Height = Math.Min(Height, area.Height - 24); Width = Math.Min(Width, area.Width - 24); Top = area.Top + (area.Height - Height) / 2; Left = area.Left + (area.Width - Width) / 2; };
        Closed += (_, _) => { rateTimer.Stop(); speedCancellation?.Cancel(); selfPage?.Dispose(); };
    }
    private async Task SampleNetworkAsync()
    {
        if (sampling || closed.IsCancellationRequested || WindowState == WindowState.Minimized) return; sampling = true;
        try
        {
            var sample = testRoot == null ? await Task.Run(() => meter.Sample()) : new NetworkSample(new("demo-wifi", "示例 Wi-Fi", "Wi-Fi"), new TrafficRate(125_000, 1_250_000));
            if (closed.IsCancellationRequested) return;
            if (displayedNetwork != sample.Connection) { rateHistory.Clear(); displayedNetwork = sample.Connection; nextForegroundProbe = default; foregroundInternet = "unknown"; foregroundNetworkVersion++; _ = RefreshStatus(); }
            NetworkNameText.Text = sample.Connection?.Name ?? "未连接网络";
            NetworkNameText.ToolTip = sample.Connection is { } connection ? $"{connection.Kind} · {connection.Name}" : "尚未发现可用的网络接口";
            RateSourceText.Text = (sample.Connection?.Kind ?? "未连接") + " · 最近 60 秒";
            DrawRates(sample.Rate);
            RateText.Text = sample.Connection == null ? "未连接网络，暂无实时速率。" : sample.Rate == null ? "正在采样当前网络…" : $"{sample.Connection.Name} · 当前接口实时流量（含其他应用）";
        }
        catch
        {
            displayedNetwork = null; rateHistory.Clear(); DrawRates(null);
            NetworkNameText.Text = "网络信息暂不可用"; NetworkNameText.ToolTip = null;
            RateSourceText.Text = "等待网络 · 最近 60 秒"; RateText.Text = "当前网络实时速率：暂不可用";
        }
        finally { sampling = false; _ = RefreshForegroundInternet(); }
    }
    private void LoadFeaturePreferences(Settings settings)
    {
        ProfilesBox.ItemsSource = store.Accounts();
        ProfilesBox.SelectedItem = ((AccountChoice[])ProfilesBox.ItemsSource).FirstOrDefault(p => p.Id == settings.ActiveProfileId);
        AutoOfflineBox.IsChecked = settings.AutoOfflineEnabled; ProtectedMacsBox.Text = string.Join(Environment.NewLine, settings.ProtectedMacs);
        OfflineOrderBox.SelectedIndex = settings.OfflineOrder == "newest" ? 1 : settings.OfflineOrder == "manual" ? 2 : 0;
        SelfPermissionBox.IsChecked = settings.SelfConsent;
    }
    private Settings ReadFeaturePreferences()
    {
        var macs = ProtectedMacsBox.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (macs.Length > 100 || macs.Any(m => QuotaRecovery.StrictMac(m) == null)) throw new UserNotice("白名单最多 100 项，请逐行填写完整有效的 MAC 地址。");
        bool enabled = AutoOfflineBox.IsChecked == true, consent = SelfPermissionBox.IsChecked == true;
        if (enabled && (macs.Length == 0 || !consent)) throw new UserNotice("启用自动处理前，请填写非空保留白名单并确认后台自助平台传输说明。");
        return new() { AutoOfflineEnabled = enabled, ProtectedMacs = macs.Select(m => QuotaRecovery.StrictMac(m)!).Distinct().ToArray(), SelfConsent = consent, OfflineOrder = OfflineOrderBox.SelectedIndex == 1 ? "newest" : OfflineOrderBox.SelectedIndex == 2 ? "manual" : "oldest" };
    }
    private void RenderFeatureStatus(AgentStatus status)
    {
        latestStatus = status;
        if (status.IsNufe && status.Authentication is "online" or "offline") PhaseText.Text = status.Authentication == "online" ? "校园网已认证" : status.Phase == "authenticating" ? "正在认证" : "校园网未认证";
        AccountStatusText.Text = "当前认证账号：" + (string.IsNullOrEmpty(status.CurrentAccount) ? "未提供" : status.CurrentAccount) + "   配置账号：" + AccountBox.Text;
        ServerDetailText.Text = (status.FailureAt is { } time ? "最近失败：" + time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine : "") +
            (string.IsNullOrEmpty(status.ServerMessage) ? "暂无服务器提示。" : status.ServerMessage);
        if (!checkingInternet) NetworkResultText.Text = status.Internet == "reachable" ? "默认路径：最近一次外网探测通过；不代表所有应用均可访问。" : status.Internet == "probe_failed" ? "默认路径：最近一次探测未通过；认证状态单独判断。" : "尚无前台外网探测结果。";
        RenderSession(); RenderShellStatus(status);
    }
    private void RenderSession()
    {
        var d = currentSession; bool same = latestStatus?.CurrentAccount is { } active && (active == sessionAccount + "@cmcc" || active == sessionAccount + "@njxy");
        SessionDataText.Text = d == null ? "本次连接：进入自助服务后自动读取；缺失字段显示为不可用。" : !same && testRoot == null ? "本次连接：自助数据账号与当前认证账号尚未匹配，未混用流量。" : $"本次连接 · 上线 {d.LoginTime}\n在线 {d.Minutes} 分钟 · 上传 {d.UpMb} MB · 下载 {d.DownMb} MB · 更新于 {sessionUpdated:HH:mm:ss}";
    }
    private async void MainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != MainTabs || closed.IsCancellationRequested) return;
        UpdateHistoryPolling();
        if (MainTabs.SelectedIndex == 0) DrawRates(null, false);
        if (MainTabs.SelectedIndex == 0 && selfPage != null) { await selfPage.RefreshConnectionDataAsync(); return; }
        if (MainTabs.SelectedItem != SelfTab) return;
        EnsureSelfPage(); await selfPage!.ActivateAsync();
    }
    private void EnsureSelfPage()
    {
        if (selfPage == null)
        {
            selfPage = new(store, testRoot, () => sessionCredential);
            selfPage.WhitelistUpdated += mac => { bool priorLoading = loadingPreferences; loadingPreferences = true; var lines = ProtectedMacsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList(); if (!lines.Any(x => QuotaRecovery.StrictMac(x) == mac)) lines.Add(mac); ProtectedMacsBox.Text = string.Join(Environment.NewLine, lines); loadingPreferences = priorLoading; UpdateConfigurationSummary(); };
            selfPage.ConsentUpdated += value => { bool wasLoading = loadingPreferences; loadingPreferences = true; SelfPermissionBox.IsChecked = value; loadingPreferences = wasLoading; };
            selfPage.SessionLoaded += (account, d) => { sessionAccount = account; currentSession = d; sessionUpdated = DateTimeOffset.Now; RenderSession(); };
            selfPage.DevicesUpdated += rows => { overviewDevices = rows; RenderOverviewDevices(); };
            selfPage.SessionStatusChanged += text => SelfBadge.Text = "自助 · " + text;
            SelfHost.Content = selfPage;
        }
    }
    private async Task StartSelfServiceAsync() { if (store.ReadSettings().SelfConsent && store.ReadSecret() != null) { EnsureSelfPage(); await selfPage!.ActivateAsync(); } }
    private void ResetSelfPage() { selfPage?.Dispose(); overviewDevices = null; RenderOverviewDevices(); SelfBadge.Text = "自助 · 未登录"; selfPage = null; SelfHost.Content = null; currentSession = null; sessionAccount = ""; SessionDataText.Text = "本次连接：尚未读取所选账号自助会话，缺失字段不作推测。"; }
    private async void SelectAccountClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (ProfilesBox.SelectedItem is not AccountChoice choice) throw new UserNotice("请选择已保存账号。");
        if (selfPage?.IsWorking == true) throw new UserNotice("请先等待或取消当前自助请求。");
        if (!await ConfirmAsync("切换配置账号", (hasUnsavedChanges ? "未保存的设置会被放弃。\n" : "") + "载入所选账号的凭据及设备规则。本次不会自动下线当前认证账号；需要更换在线账号时使用手动重新认证。确认载入？")) return;
        AgentResponse? agent = await AgentForConfiguration();
        if (agent != null && agent.Status.ProtocolVersion < 2) throw new UserNotice("旧版后台仍在运行，本次不会切换保存的账号；请日后手动更新后台。");
        bool previousPaused = store.ReadSettings().Memory.PausedByTool;
        if (agent != null) await StopAgent();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(65));
            using var lease = await RunnerLease.AcquireAsync(store, timeout.Token);
            await Task.Run(() => store.SelectProfile(choice.Id, previousPaused));
            store.SaveLastCheck(new() { Phase = "waiting" });
            await ConfigureKeepAliveTask(store.ReadSettings());
        }
        sessionCredential = null; ResetSelfPage(); CampusPassword.Clear();
        await LoadPreferences(); if (agent != null) await EnsureAgent();
        SettingsSaved(); Feedback.Text = "已载入所选账号，未下线当前连接。"; await RefreshStatus();
    });
    private void NewAccountClick(object sender, RoutedEventArgs e) { AccountBox.Clear(); CampusPassword.Clear(); ProtectedMacsBox.Clear(); AutoOfflineBox.IsChecked = false; OfflineOrderBox.SelectedIndex = 0; CredentialNote.Text = "填写新学号和密码后保存；新账号默认关闭设备自动处理。"; }
    private async Task Authenticate(bool resume) => await Run(async () =>
    {
        if (sessionCredential != null) { await AuthenticateSessionOnce(); return; }
        await EnsureAgent(); var status = await Call("status");
        if (status.Status.ProtocolVersion < 2) throw new UserNotice("当前运行的是旧版后台，不支持新认证入口。新版位于独立预览目录；本次不会替换或重启旧后台。");
        var result = await Call(resume ? "authenticate_resume" : "authenticate_once", 60);
        RenderFeatureStatus(result.Status); PhaseText.Text = Texts.Phase(result.Status.Phase); IssueText.Text = Texts.Issue(result.Status.Issue);
        Feedback.Text = result.Ok ? resume && result.Status.KeepAliveEnabled ? "认证已确认，保活已恢复。" : !result.Status.KeepAliveEnabled ? "认证已确认。后台保活未启用，可在运行偏好中设置。" : "认证已确认，原保活暂停状态保留。" : Texts.Issue(result.Code);
        await RefreshStatus();
    });
    private async void AuthenticateResumeClick(object s, RoutedEventArgs e) => await Authenticate(true);
    private async void AuthenticateOnceClick(object s, RoutedEventArgs e) => await Authenticate(false);
    private async void RecoverAutoClick(object s, RoutedEventArgs e) => await Run(async () =>
    {
        var status = await Call("status"); if (status.Status.ProtocolVersion < 2) throw new UserNotice("旧版后台不支持此操作，本次未修改。");
        await Call("auto_recover"); Feedback.Text = "已请求手动恢复自动规则；空名单或未启用的规则仍不会执行。";
    });
    private async void CheckInternetClick(object s, RoutedEventArgs e)
    {
        if (checkingInternet) return; checkingInternet = true;
        try
        {
            if (testRoot != null) { NetworkResultText.Text = "隔离示例：探测通过（未访问外网）。"; return; }
            bool direct = NetworkPathBox.SelectedIndex == 1; NetworkResultText.Text = "正在轻量检测…";
            using var probe = new InternetProbe(direct); bool ok = await probe.CheckAsync(closed.Token);
            NetworkResultText.Text = (direct ? "校园网源地址绑定（VPN 绕过未确认）" : "默认网络路径") + "：" + (ok ? "探测通过" : "探测未通过") + " · " + DateTimeOffset.Now.ToString("HH:mm:ss") + "；不会自动下线重登。";
        }
        finally { checkingInternet = false; }
    }
    private async void SpeedClick(object s, RoutedEventArgs e)
    {
        if (measuring) return; measuring = true; speedCancellation = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        try
        {
            if (testRoot != null) { SpeedResultText.Text = "隔离示例：下载 12.5 Mbps · 上传 5 Mbps · HTTP 延迟 20 ms（合成结果，未访问外网）。"; return; }
            using var measurement = new SpeedMeasurement(NetworkPathBox.SelectedIndex == 1);
            var result = await measurement.RunAsync(SpeedModeBox.SelectedIndex == 1, new Progress<string>(v => SpeedResultText.Text = v), speedCancellation.Token);
            string Value(double? n) => n is { } value ? value.ToString("0.##") : "未完成";
            SpeedResultText.Text = $"{result.Path}\n下载 {Value(result.DownloadMbps)} Mbps · 上传 {Value(result.UploadMbps)} Mbps · HTTP 首部延迟 {Value(result.LatencyMs)} ms\n有效载荷 {result.PayloadBytes / 1_000_000.0:0.##} MB · {(result.Limited ? "已达到时间或流量限制，部分指标未完成" : "本次请求完成，结果仅作参考")}";
        }
        catch (OperationCanceledException) { SpeedResultText.Text = "测速已取消，未完成的指标不会填零。"; }
        catch { SpeedResultText.Text = "测速服务或路径不可达，本次未获得完整结果；不会自动重复测速。"; }
        finally { measuring = false; speedCancellation.Dispose(); speedCancellation = null; }
    }
    private void CancelSpeedClick(object s, RoutedEventArgs e) => speedCancellation?.Cancel();
}
