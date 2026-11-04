using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NufeCampus.SelfService;
using NufeCampus.Windows;
namespace NufeCampus.App;

public partial class MainWindow
{
    private bool shellReady, loadingPreferences, hasUnsavedChanges;
    private string navigationGroup = "overview";
    private readonly Dictionary<string, int> selectedSections = new() { ["devices"] = 0, ["records"] = 0, ["services"] = 0, ["settings"] = 0 };
    private IReadOnlyList<OnlineDevice>? overviewDevices;
    private TaskCompletionSource<bool>? confirmation;
    private IInputElement? confirmationFocus;
    private readonly Queue<(DateTimeOffset Time, TrafficRate? Rate)> rateHistory = new();

    private void InitializeShell()
    {
        shellReady = true;
        OverviewNav.IsChecked = true;
        Loaded += (_, _) =>
        {
            foreach (var text in new[] { AccountBox, ProtectedMacsBox }) text.TextChanged += (_, _) => MarkUnsaved();
            CampusPassword.PasswordChanged += (_, _) => MarkUnsaved();
            CheckIntervalBox.TextChanged += (_, _) => MarkUnsaved();
            foreach (var box in new[] { EnableBox, AutoStartBox, HttpConsent, AutoOfflineBox, SelfPermissionBox })
            {
                box.Checked += (_, _) => MarkUnsaved(); box.Unchecked += (_, _) => MarkUnsaved();
            }
            CarrierBox.SelectionChanged += (_, _) => MarkUnsaved(); OfflineOrderBox.SelectionChanged += (_, _) => MarkUnsaved();
            MbpsBox.Checked += (_, _) => DrawRates(null, false); MbpsBox.Unchecked += (_, _) => DrawRates(null, false);
        };
        Closed += (_, _) => confirmation?.TrySetResult(false);
    }
    private void MarkUnsaved()
    {
        if (!IsLoaded || loadingPreferences) return;
        hasUnsavedChanges = true; SaveBar.Visibility = Visibility.Visible;
    }
    private void SettingsSaved() { hasUnsavedChanges = false; SaveBar.Visibility = Visibility.Collapsed; UpdateConfigurationSummary(); }
    private void UpdateConfigurationSummary()
    {
        ConfigurationText.Text = "当前配置账号 · " + (string.IsNullOrWhiteSpace(AccountBox.Text) ? "尚未保存" : AccountBox.Text);
        var settings = store.ReadSettings();
        ProtectionSummary.Text = $"白名单已保留 {settings.ProtectedMacs.Length} 台设备 · 超限自动处理{(settings.AutoOfflineEnabled ? "已启用" : "未启用")}";
        RenderOverviewDevices();
    }
    private void NavigationChecked(object sender, RoutedEventArgs e)
    {
        if (shellReady && sender is RadioButton { Tag: string group }) Navigate(group);
    }
    private void Navigate(string group, int? section = null)
    {
        if (section.HasValue) selectedSections[group] = section.Value;
        navigationGroup = group;
        var names = new Dictionary<string, (string Title, string English)>
        {
            ["overview"] = ("网络概览", "OVERVIEW"),
            ["keepalive"] = ("保活记录", "KEEPALIVE HISTORY"),
            ["devices"] = ("我的设备", "DEVICES"),
            ["records"] = ("用量与记录", "ACTIVITY"),
            ["services"] = ("业务管理", "SERVICES"),
            ["settings"] = ("设置", "SETTINGS")
        };
        PageTitle.Text = names[group].Title; PageEyebrow.Text = "WORKSPACE / " + names[group].English;
        SectionNav.Children.Clear(); SectionNavBorder.Visibility = group is "overview" or "keepalive" ? Visibility.Collapsed : Visibility.Visible;
        string[] labels = group switch
        {
            "devices" => ["在线设备", "绑定设备", "白名单保护"],
            "records" => ["上网记录", "账号与账单", "业务记录"],
            "services" => ["运营商管理", "报停与复通"],
            "settings" => ["校园网账号", "运行偏好", "诊断与备份", "联网与测速"],
            _ => []
        };
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var tab = new RadioButton { Content = labels[i], GroupName = "Sections", Style = (Style)FindResource("SectionButton"), IsChecked = selectedSections[group] == i };
            System.Windows.Automation.AutomationProperties.SetName(tab, labels[i]);
            tab.Checked += (_, _) => { selectedSections[group] = index; SelectSection(); };
            SectionNav.Children.Add(tab);
        }
        SelectSection();
    }
    private void NavigateFromShortcut(string group, int section = 0)
    {
        selectedSections[group] = section;
        var nav = group switch { "devices" => DevicesNav, "records" => RecordsNav, "services" => ServicesNav, "settings" => SettingsNav, _ => OverviewNav };
        if (nav.IsChecked == true) Navigate(group); else nav.IsChecked = true;
    }
    private void SelectSection()
    {
        if (navigationGroup == "overview") { MainTabs.SelectedIndex = 0; return; }
        if (navigationGroup == "keepalive") { MainTabs.SelectedItem = KeepAliveHistoryTab; return; }
        int section = selectedSections[navigationGroup];
        if (navigationGroup == "settings") { MainTabs.SelectedIndex = new[] { 1, 2, 3, 6 }[section]; return; }
        if (navigationGroup == "devices" && section == 2) { MainTabs.SelectedIndex = 5; return; }
        EnsureSelfPage();
        int page = navigationGroup switch { "devices" => section == 0 ? 0 : 4, "records" => new[] { 1, 2, 5 }[section], _ => section == 0 ? 3 : 6 };
        selfPage!.SelectPage(page); MainTabs.SelectedItem = SelfTab;
    }
    private void OpenDevicesClick(object sender, RoutedEventArgs e) => NavigateFromShortcut("devices");
    private void OpenProtectionClick(object sender, RoutedEventArgs e) => NavigateFromShortcut("devices", 2);
    private void OpenSpeedClick(object sender, RoutedEventArgs e) => NavigateFromShortcut("settings", 3);
    private void MoreConnectionClick(object sender, RoutedEventArgs e) => ConnectionDetails.IsExpanded = !ConnectionDetails.IsExpanded;
    private async void ConnectionDetailsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, ConnectionDetails)) return;
        bool expanded = ConnectionDetails.IsExpanded;
        MoreConnectionButton.Content = expanded ? "收起连接操作 ⌃" : "更多连接操作 ⌄";
        // Wait for the expander to participate in layout before scrolling to it.
        await Dispatcher.InvokeAsync(() =>
        {
            if (closed.IsCancellationRequested || ConnectionDetails.IsExpanded != expanded) return;
            if (expanded) ConnectionDetails.BringIntoView();
            else { MoreConnectionButton.BringIntoView(); MoreConnectionButton.Focus(); }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private async void MainConnectionClick(object sender, RoutedEventArgs e)
    {
        if (sessionCredential != null) { await Run(AuthenticateSessionOnce); return; }
        if (!store.HasSecret()) { NavigateFromShortcut("settings"); AccountBox.Focus(); return; }
        if (latestStatus == null) { StartClick(sender, e); return; }
        if (!latestStatus.KeepAliveEnabled) { await Authenticate(false); return; }
        if (latestStatus.Paused) await Send("resume");
        else if (latestStatus.Authentication == "online" && latestStatus.KeepAliveEnabled) await Send("pause");
        else await Authenticate(true);
    }
    private void RenderShellStatus(AgentStatus? status)
    {
        bool hasCredentials = status?.HasCredentials ?? foregroundHasCredentials;
        AuthBadge.Text = "认证 · " + (status?.Authentication switch { "online" => "已认证", "offline" => "未认证", _ => "未确认" });
        InternetBadge.Text = "外网 · " + (status?.Internet switch { "reachable" => "可访问", "probe_failed" => "探测未通过", _ => "未检测" });
        KeeperBadge.Text = "保活 · " + (status == null ? "后台未运行" : status.Paused ? "已暂停" : !status.KeepAliveEnabled ? "已关闭" : !status.IsNufe ? "离校静默" : "运行中");
        ConnectionHint.Text = status == null ? "后台已退出；不会因打开窗口自动启动。" : status.Paused ? "暂停会跨重启保留，需要手动恢复。" : status.Authentication == "online" ? "校园认证已确认，外网与自助状态单独显示。" : "只在 NUFE-STU 上自动认证，其他网络保持静默。";
        MainConnectionButton.Content = status == null ? "启动后台" : status.Paused ? "恢复保活" : status.Authentication == "online" && status.KeepAliveEnabled ? "暂停保活" : "认证并恢复保活";
        if (status != null && !status.KeepAliveEnabled) MainConnectionButton.Content = "认证一次";
        if (sessionCredential != null)
        {
            MainConnectionButton.Content = "认证一次";
            KeeperBadge.Text = "保活 · 需要加密保存";
            if (status == null) PhaseText.Text = "账号已就绪 · 仅本次使用";
            IssueText.Text = "点击“认证一次”手动连接；后台保活需要加密保存账号密码。";
            ConnectionHint.Text = "当前账号仅本次使用；关闭窗口不保留密码，不运行后台保活。";
        }
        else if (!hasCredentials)
        {
            MainConnectionButton.Content = "设置校园网账号";
            ConnectionHint.Text = "请先填写账号密码，并选择是否加密保存。";
        }
        if (foregroundStrategy == "scheduler" && sessionCredential == null && hasCredentials)
        {
            KeeperBadge.Text = "任务保活 · " + (status?.Paused == true ? "已暂停" : status?.KeepAliveEnabled == true ? "已配置" : "已停止");
            ConnectionHint.Text = "前台独立实时查询认证状态；任务计划按设置间隔保活。关闭窗口不停止任务，退出程序及后台会停止任务。";
            if (status?.CheckedAt == default) UpdatedText.Text = "任务尚无检查结果";
        }
        MainConnectionButton.IsEnabled = !busy;
        ConnectionGlyph.Text = status?.Authentication == "online" ? "\uE73E" : status?.Paused == true ? "\uE769" : "\uE701";
        UpdatedText.Text = status == null ? "后台状态未更新" : status.CheckedAt == default ? "尚无检查结果" : "最近检查 · " + status.CheckedAt.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
    }
    private sealed record OverviewDevice(string Name, string Ip, string LoginTime, string Protection);
    private void RenderOverviewDevices()
    {
        if (overviewDevices == null)
        {
            OverviewDevicesGrid.ItemsSource = null; OverviewDevicesTitle.Text = "在线设备"; DeviceCountBadge.Text = "—";
            OverviewDevicesInfo.Text = "数据尚未加载或自助会话不可用，未将其显示为零台。"; return;
        }
        var settings = store.ReadSettings();
        OverviewDevicesGrid.ItemsSource = overviewDevices.Select(d => new OverviewDevice(
            string.IsNullOrWhiteSpace(d.Host) ? (string.IsNullOrWhiteSpace(d.Type) ? "未命名设备" : d.Type) : d.Host,
            d.Ip, d.LoginTime, QuotaRecovery.StrictMac(d.Mac) is { } mac && settings.ProtectedMacs.Any(m => QuotaRecovery.StrictMac(m) == mac) ? "白名单" : "未加入")).ToArray();
        OverviewDevicesTitle.Text = $"在线设备 · {overviewDevices.Count} 台"; DeviceCountBadge.Text = overviewDevices.Count.ToString();
        OverviewDevicesInfo.Text = $"自助账号 {sessionAccount} · 本次会话缓存，需更新请在设备页刷新。";
    }
    private void DrawRates(TrafficRate? sample, bool append = true)
    {
        if (append) { rateHistory.Enqueue((DateTimeOffset.UtcNow, sample)); while (rateHistory.Count > 60) rateHistory.Dequeue(); }
        else if (rateHistory.Count > 0) sample = rateHistory.Last().Rate;
        // Continue collecting history on other pages, but only format and draw visible controls.
        if (MainTabs.SelectedIndex != 0) return;
        bool mbps = MbpsBox.IsChecked == true; double factor = mbps ? 8.0 / 1_000_000 : 1.0 / 1_000_000;
        DownloadRate.Text = sample == null ? "—" : (sample.Download * factor).ToString("0.##");
        UploadRate.Text = sample == null ? "—" : (sample.Upload * factor).ToString("0.##");
        DownloadUnit.Text = UploadUnit.Text = mbps ? "Mbps" : "MB/s";
        DrawRateChart();
    }
    private void RateChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawRateChart();
    private void DrawRateChart()
    {
        if (!shellReady || !RateChart.IsVisible || WindowState == WindowState.Minimized || RateChart.ActualWidth <= 0) return;
        double width = RateChart.ActualWidth, height = RateChart.ActualHeight;
        RateChart.Children.Clear();
        foreach (double y in new[] { 10.0, height / 2, height - 10 }) RateChart.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = (Brush)FindResource("LineBrush"), StrokeThickness = 1 });
        var history = rateHistory.ToArray(); if (history.Length == 0) return;
        var rates = history.Where(h => h.Rate != null).Select(h => Math.Max(h.Rate!.Download, h.Rate.Upload)).ToArray();
        if (rates.Length == 0) return;
        double ceiling = Math.Max(1024, rates.Max() * 1.15); var now = DateTimeOffset.UtcNow;
        foreach (bool down in new[] { true, false })
        {
            Polyline? line = null;
            foreach (var point in history)
            {
                if (point.Rate == null) { line = null; continue; }
                if (line == null)
                {
                    line = new Polyline { Stroke = down ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(153, 162, 191)), StrokeThickness = down ? 2 : 1.5 };
                    if (!down) line.StrokeDashArray = new DoubleCollection { 4, 4 }; RateChart.Children.Add(line);
                }
                double x = Math.Clamp(1 - (now - point.Time).TotalSeconds / 60, 0, 1) * width;
                double value = down ? point.Rate.Download : point.Rate.Upload;
                line.Points.Add(new Point(x, height - 10 - value / ceiling * (height - 20)));
            }
        }
    }
    internal Task<bool> ConfirmAsync(string title, string message)
    {
        if (confirmation != null) return Task.FromResult(false);
        confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously); confirmationFocus = Keyboard.FocusedElement;
        DialogTitle.Text = title; DialogMessage.Text = message; DialogOverlay.Visibility = Visibility.Visible;
        DialogCancel.Focus(); return confirmation.Task;
    }
    private void CompleteConfirmation(bool result)
    {
        var pending = confirmation; confirmation = null; DialogOverlay.Visibility = Visibility.Collapsed;
        DialogMessage.Text = ""; confirmationFocus?.Focus(); pending?.TrySetResult(result);
    }
    private void DialogCancelClick(object sender, RoutedEventArgs e) => CompleteConfirmation(false);
    private void DialogConfirmClick(object sender, RoutedEventArgs e) => CompleteConfirmation(true);
    private void WindowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && confirmation != null) { CompleteConfirmation(false); e.Handled = true; } }
}
