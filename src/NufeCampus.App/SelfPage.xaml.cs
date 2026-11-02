using NufeCampus.SelfService;
using NufeCampus.Windows;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
namespace NufeCampus.App;

public partial class SelfPage : System.Windows.Controls.UserControl, IDisposable
{
    private readonly LocalStore store;
    private readonly string? testRoot;
    private readonly SelfClient client;
    private readonly Func<SecretPair?>? sessionCredential;
    private bool SessionOnly => sessionCredential?.Invoke() != null;
    private SecretPair? AvailableCredential() => sessionCredential?.Invoke() ?? store.SharedCredential();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private bool busy, writing;
    private string pendingWrite = "";
    private int historyPage = 1, billPage = 1, historyTotal, billTotal;
    private DateOnly historyStart, historyEnd;
    private int year;
    private bool loginBlocked, reloadPending, disposed, signedOut;
    private bool captchaRequired;
    private readonly HashSet<int> loadedPages = [];
    private readonly Dictionary<int, string> pageErrors = [];
    private bool prefetched;
    private int filterVersion;
    private int boundPage = 1, boundTotal;
    public static readonly DependencyProperty ProtectedMacsViewProperty = DependencyProperty.Register(nameof(ProtectedMacsView), typeof(string[]), typeof(SelfPage), new PropertyMetadata(Array.Empty<string>()));
    public string[] ProtectedMacsView { get => (string[])GetValue(ProtectedMacsViewProperty); private set => SetValue(ProtectedMacsViewProperty, value); }
    public bool IsWorking => busy;
    public bool IsWriting => busy && writing;
    public event Action<IReadOnlyList<OnlineDevice>?>? DevicesUpdated;
    public event Action<string>? SessionStatusChanged;
    private void ShowLoginClick(object s, RoutedEventArgs e) { LoginPanel.Visibility = LoginPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; LoginPanel.IsExpanded = true; }
    public void SelectPage(int index) { if (index < 0 || index >= PageNames.Length) throw new ArgumentOutOfRangeException(nameof(index)); DataTabs.SelectedIndex = index; }
    private Task<bool> ConfirmAsync(string title, string message) => Window.GetWindow(this) is MainWindow main ? main.ConfirmAsync(title, message) : Task.FromResult(false);
    public event Action<string, OnlineDevice?>? SessionLoaded;
    public event Action<bool>? ConsentUpdated;
    private readonly System.Windows.Threading.DispatcherTimer filters = new() { Interval = TimeSpan.FromMilliseconds(350) };
    public SelfPage(LocalStore store, string? testRoot, Func<SecretPair?>? sessionCredential = null)
    {
        InitializeComponent(); this.store = store; this.testRoot = testRoot; this.sessionCredential = sessionCredential;
        if (SessionOnly)
        {
            RememberSelf.IsChecked = false; RememberSelf.IsEnabled = false;
            UseSaved.Content = "密码留空时使用当前窗口的同一账号账密（不保存）";
            RememberSelf.Content = "当前为仅本次使用，不更新本机保存的凭据";
        }
        client = testRoot == null ? new() : new(new SelfDemoHandler(), () => new("nufe", "192.0.2.1", "001122334455"));
        var settings = store.ReadSettings(); ProtectedMacsView = settings.ProtectedMacs; SelfConsent.IsChecked = settings.SelfConsent;
        historyStart = DateOnly.FromDateTime(DateTime.Today.AddDays(-29)); historyEnd = DateOnly.FromDateTime(DateTime.Today);
        StartDate.SelectedDate = historyStart.ToDateTime(TimeOnly.MinValue); EndDate.SelectedDate = DateTime.Today;
        BusinessStart.SelectedDate = StartDate.SelectedDate; BusinessEnd.SelectedDate = EndDate.SelectedDate; ReopenDate.SelectedDate = DateTime.Today.AddDays(1);
        year = DateTime.Today.Year; BillYear.Text = year.ToString();
        filters.Tick += async (_, _) => { filters.Stop(); await LoadCurrentAsync(); };
        Loaded += async (_, _) => await ActivateAsync();
        if (testRoot != null) SessionText.Text = "隔离示例：本地合成响应，不访问学校。";
    }
    public void Dispose() { if (disposed) return; disposed = true; filters.Stop(); lifetime.Cancel(); operation?.Cancel(); ClearPageData(); client.Dispose(); SelfPassword.Clear(); ClearOperatorPasswords(); CaptchaImage.Source = null; DevicesGrid.ItemsSource = null; HistoryGrid.ItemsSource = null; BillsGrid.ItemsSource = null; BoundGrid.ItemsSource = null; }
    public async Task ActivateAsync()
    {
        if (disposed) return; if (busy) { reloadPending = true; return; }
        await Run(async token =>
        {
            if (!client.Authenticated)
            {
                if (SelfConsent.IsChecked != true) { SelfFeedback.Text = "首次使用请在本页确认自助平台传输说明，随后自动登录。"; return; }
                var saved = AvailableCredential();
                if (saved == null || signedOut || loginBlocked || (!SessionOnly && store.ReadSettings().SelfLoginBlocked))
                {
                    SelfFeedback.Text = saved == null ? "未保存校园凭据，请在本页登录或到账户页保存。" : signedOut ? "已主动退出自助登录，本窗口不再自动登录；手动登录一次可恢复。" : "自动登录已停止，请修正凭据或手动登录一次。"; return;
                }
                var challenge = await Prepare(token); SelfAccount.Text = saved.Account;
                if (challenge.CaptchaRequired) { SelfFeedback.Text = "平台要求验证码，请填写后登录一次。"; return; }
                try { await client.LoginAsync(saved, "", token); } catch (SelfException e) when (e.Code == "login") { loginBlocked = true; if (!SessionOnly) store.UpdateSettings(s => s with { SelfLoginBlocked = true }); throw; } catch { loginBlocked = true; throw; }
                LoggedIn();
            }
            if (!prefetched) await Prefetch(token); else await LoadSelected(token);
        });
    }
    private async void ConsentChanged(object sender, RoutedEventArgs e) { if (!IsLoaded) return; bool consent = SelfConsent.IsChecked == true; client.Consent = consent; if (!consent) operation?.Cancel(); store.UpdateSettings(s => s with { SelfConsent = consent }); ConsentUpdated?.Invoke(consent); if (consent) await ActivateAsync(); }
    private async Task<LoginChallenge> Prepare(CancellationToken token)
    {
        var challenge = await client.PrepareLoginAsync(token); captchaRequired = challenge.CaptchaRequired; CaptchaCode.Clear(); CaptchaCode.IsEnabled = captchaRequired; CaptchaDecoration.Visibility = captchaRequired ? Visibility.Collapsed : Visibility.Visible; CaptchaImage.Visibility = captchaRequired ? Visibility.Visible : Visibility.Collapsed; CaptchaDecoration.Text = Random.Shared.Next(2) == 0 ? "KSKBL" : "ZDJD";
        try { using var stream = new MemoryStream(challenge.Captcha); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); CaptchaImage.Source = image; } catch { CaptchaImage.Source = null; }
        return challenge;
    }
    private void LoggedIn() { loginBlocked = false; signedOut = false; prefetched = false; loadedPages.Clear(); pageErrors.Clear(); ClearPageData(); if (!SessionOnly && client.AuthenticatedAccount == store.ReadSecret()?.Account) store.UpdateSettings(s => s with { SelfLoginBlocked = false }); SessionText.Text = (testRoot == null ? "自助已登录" : "隔离合成会话") + " · 账号 " + SelfAccount.Text + (SessionOnly ? " · 仅本次使用" : " · 与校园认证独立"); LoginPanel.IsExpanded = false; LoginPanel.Visibility = Visibility.Collapsed; }
    private async void SelfLogoutClick(object s, RoutedEventArgs e) => await Run(async t => { signedOut = true; try { await client.LogoutAsync(t); SelfFeedback.Text = "已退出自助登录；本窗口不再自动登录，手动登录一次可恢复。校园认证及保活不受影响。"; } finally { SessionLoaded?.Invoke("", null); } });
    private async Task LoadPage(int index, CancellationToken token)
    {
        if (index < 0 || index >= PageNames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        int version = filterVersion;
        switch (index)
        {
            case 0: await Devices(token); break;
            case 1: (historyStart, historyEnd) = Dates(); await History(token); break;
            case 2: year = Year(); await Overview(token); await Bills(token); break;
            case 3: await Operators(token); break;
            case 4: await Bound(token); break;
            case 5: await Business(token); break;
            case 6: await AccountServices(token); break;
        }
        if (version == filterVersion) loadedPages.Add(index); else loadedPages.Remove(index); pageErrors.Remove(index); pageTimes[index] = DateTimeOffset.Now;
        if (prefetched) UpdatePrefetchStatus();
    }
    private readonly Dictionary<int, DateTimeOffset> pageTimes = [];
    private async Task LoadSelected(CancellationToken token) { int index = Math.Max(0, DataTabs.SelectedIndex); if (!loadedPages.Contains(index)) { if (pageErrors.TryGetValue(index, out var error)) { SelfFeedback.Text = error + "；点击本页刷新可重试。"; return; } await LoadPage(index, token); } SelfFeedback.Text = "本次会话数据 · 更新于 " + pageTimes[index].ToString("HH:mm:ss") + " · 切页不重复查询，需更新请刷新。"; }
    private static readonly string[] PageNames = ["在线设备", "上网记录", "账号账单", "运营商", "绑定设备", "业务记录", "报停复通"];
    private void UpdatePrefetchStatus()
    {
        string failures = string.Join("、", pageErrors.Keys.Order().Select(i => PageNames[i]));
        PrefetchText.Text = $"会话数据：{loadedPages.Count}/{PageNames.Length} 页已加载" + (failures.Length == 0 ? "，切页直接查看。" : $"；未加载：{failures}，可单独刷新。");
    }
    private async Task Prefetch(CancellationToken token)
    {
        for (int i = 0; i < PageNames.Length; i++) { token.ThrowIfCancellationRequested(); if (!client.Authenticated) break; PrefetchText.Text = $"登录后预取 {i + 1}/{PageNames.Length}：{PageNames[i]}…"; try { await LoadPage(i, token); } catch (OperationCanceledException) { throw; } catch (PageReadException e) { pageErrors[i] = e.Display; } catch (SelfException e) { pageErrors[i] = SelfData.Message(e.Code); } catch { pageErrors[i] = "该页加载失败，其他页面继续加载"; } }
        prefetched = true; UpdatePrefetchStatus(); await LoadSelected(token);
    }
    private async Task LoadCurrentAsync() { if (!client.Authenticated) return; await Run(LoadSelected); }
    private async void DataTabChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e) { if (!IsLoaded || e.Source != DataTabs) return; await ActivateAsync(); }
    private void FilterChanged(object s, RoutedEventArgs e) { if (!IsLoaded) return; historyPage = billPage = 1; filterVersion++; int index = ReferenceEquals(s, BillYear) ? 2 : 1; loadedPages.Remove(index); pageErrors.Remove(index); if (index == 1) { HistoryGrid.ItemsSource = null; HistoryInfo.Text = "筛选已变化，正在重新加载全部记录…"; } filters.Stop(); filters.Start(); }
    private async Task Run(Func<CancellationToken, Task> work, bool write = false)
    {
        if (disposed) return; if (busy) { if (!write) reloadPending = true; return; }
        if (write && pendingWrite.Length != 0) { SelfFeedback.Text = "上次写入尚待核对，请先只读刷新相应设备或绑定。"; return; }
        busy = true; writing = write; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation.CancelAfter(TimeSpan.FromMinutes(10)); SelfFeedback.Text = "正在处理…";
        try { client.Consent = SelfConsent.IsChecked == true; await work(operation.Token); }
        catch (PageReadException e) { SelfFeedback.Text = e.Display; }
        catch (SelfException e) { SelfFeedback.Text = SelfData.Message(e.Code) + " · 数据未更新"; }
        catch (OperationCanceledException) { SelfFeedback.Text = "请求已取消或超时。写入可能已提交时，请先只读刷新核对；不会自动重发。"; }
        catch { SelfFeedback.Text = SelfData.Message("network"); }
        finally { busy = false; operation.Dispose(); operation = null; if (!client.Authenticated) { prefetched = false; loadedPages.Clear(); pageErrors.Clear(); ClearPageData(); LoginPanel.Visibility = Visibility.Visible; LoginPanel.IsExpanded = true; SessionText.Text = testRoot == null ? "尚未登录自助平台或会话已失效。保活不受影响。" : "隔离示例 · 尚未登录"; } SessionStatusChanged?.Invoke(client.Authenticated ? "已登录" : signedOut ? "已退出" : "需要处理"); if (reloadPending && !disposed) { reloadPending = false; await LoadCurrentAsync(); } }
    }
    private async void PrepareClick(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var challenge = await Prepare(token); CaptchaCode.Clear();
        try { using var stream = new MemoryStream(challenge.Captcha); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); CaptchaImage.Source = image; } catch { CaptchaImage.Source = null; }
        SelfFeedback.Text = challenge.CaptchaRequired ? "请填写验证码，再点击登录一次。" : "新会话已准备；若图片要求验证码请填写，再点击登录一次。";
    });
    private SecretPair CampusSecret()
    {
        string account = SelfAccount.Text.Trim(), password = SelfPassword.Password;
        if (password.Length == 0 && UseSaved.IsChecked == true) { var campus = AvailableCredential(); if (account.Length == 0) account = campus?.Account ?? ""; if (campus?.Account == account) return campus; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(account, @"^\d{6,16}$") || password.Length == 0) throw new SelfException("login"); return new(account, password);
    }
    private async void LoginClick(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var secret = CampusSecret(); if (!client.HasLoginChallenge) await Prepare(token); if (captchaRequired && CaptchaCode.Text.Trim().Length == 0) { SelfFeedback.Text = "平台要求验证码，请填写图片中的验证码后再登录；尚未提交密码。"; return; }
        try { await client.LoginAsync(secret, CaptchaCode.Text.Trim(), token); } catch (SelfException failure) when (failure.Code == "login") { loginBlocked = true; if (!SessionOnly && secret.Account == store.ReadSecret()?.Account) store.UpdateSettings(s => s with { SelfLoginBlocked = true }); throw; } catch { loginBlocked = true; throw; } finally { SelfPassword.Clear(); CaptchaCode.Clear(); }
        if (!SessionOnly && RememberSelf.IsChecked == true && secret.Account == store.ReadSecret()?.Account) store.SaveSharedCredential(secret);
        LoggedIn(); await Prefetch(token);
    });
    private async Task Devices(CancellationToken token) { var rows = await client.OnlineAsync(token); ProtectedMacsView = store.ReadSettings().ProtectedMacs; DevicesGrid.ItemsSource = rows; var own = testRoot == null ? Wifi.Read() : new WifiState("nufe", "192.0.2.1", "001122334455"); SessionLoaded?.Invoke(client.AuthenticatedAccount, rows.SingleOrDefault(d => Wifi.NormalizeMac(d.Mac) == Wifi.NormalizeMac(own.Mac ?? "") && d.Ip == own.Address)); DevicesUpdated?.Invoke(rows); if (pendingWrite == "devices") pendingWrite = ""; SelfFeedback.Text = "在线设备已刷新。此列表仅表示当前在线会话，不是全部绑定设备。"; }
    public async Task RefreshConnectionDataAsync() { if (client.Authenticated && !busy) await Run(Devices); }
    private async void DevicesClick(object sender, RoutedEventArgs e) => await Run(t => LoadPage(0, t));
    private async void OfflineClick(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        if (DevicesGrid.SelectedItem is not OnlineDevice selected) { SelfFeedback.Text = "请选择一台设备。"; return; }
        var ticket = await client.PrepareOfflineAsync(selected, token); bool own = testRoot != null ? selected.Mac == "001122334455" : Wifi.NormalizeMac(selected.Mac) == Wifi.NormalizeMac(Wifi.Read().Mac ?? "");
        string mask = selected.Mac.Length > 4 ? "***" + selected.Mac[^4..] : "已隐藏";
        if (!await ConfirmAsync("设备下线确认", $"将下线当前账号的一条在线会话：\nMAC 尾部：{mask}\n登录时间：{selected.LoginTime}\n{(own ? "这是当前电脑，将先持久暂停保活，直到你手动恢复。" : "这不是当前电脑，本机保活设置不变。")}\nHTTP 目的地：10.100.0.3:8080\n确认下线？")) { SelfFeedback.Text = "已取消，未提交。"; return; }
        using var keepAliveLease = own && store.ReadSettings().KeepAliveStrategy == "scheduler" ? await RunnerLease.AcquireAsync(store, token) : null;
        if (own)
        {
            if (store.ReadSettings().KeepAliveStrategy == "scheduler") store.UpdateSettings(x => x with { Memory = x.Memory with { PausedByTool = true } });
            else { var paused = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(testRoot), "pause", token); if (!paused.Ok) throw new SelfException("changed"); }
        }
        pendingWrite = "devices"; await client.OfflineAsync(ticket, token); await Devices(token); SelfFeedback.Text = own ? "本机会话已确认下线；保活保持暂停，需回主界面手动恢复。" : "选中会话已确认下线。";
    }, true);
    private (DateOnly Start, DateOnly End) Dates() { if (StartDate.SelectedDate is not { } start || EndDate.SelectedDate is not { } end || end < start || end.Subtract(start).TotalDays > 3660) throw new SelfException("range"); return (DateOnly.FromDateTime(start), DateOnly.FromDateTime(end)); }
    private Task<Page<HistoryRow>> CompleteHistory(DateOnly start, DateOnly end, CancellationToken token) => testRoot == null ? OfficialHistory.LoadAsync(client, start, end, token) : client.HistoryRangeAsync(start, end, token);
    private async Task History(CancellationToken token) { var page = await CompleteHistory(historyStart, historyEnd, token); historyTotal = page.Total; HistoryGrid.ItemsSource = page.Rows; HistoryInfo.Text = $"已加载全部 {page.Total} 条 · {historyStart:yyyy-MM-dd} 至 {historyEnd:yyyy-MM-dd} · " + (testRoot == null ? "来源：官网全部记录导出" : "隔离合成数据"); HistorySummary.Text = "筛选汇总：" + page.Summary; SelfFeedback.Text = "筛选内全部记录已载入；当前在线会话见在线设备页。"; }
    private async void HistoryClick(object s, RoutedEventArgs e) => await Run(async t => { (historyStart, historyEnd) = Dates(); historyPage = 1; await LoadPage(1, t); });
    private async void HistoryPrev(object s, RoutedEventArgs e) => await Run(async t => { if (historyPage > 1) { historyPage--; await History(t); } });
    private async void HistoryNext(object s, RoutedEventArgs e) => await Run(async t => { if (historyPage * 10 < historyTotal) { historyPage++; await History(t); } });
    private int Year() { if (!int.TryParse(BillYear.Text, out int result) || result < 2000 || result > 2100) throw new SelfException("page"); return result; }
    private async Task Bills(CancellationToken token) { var page = await client.BillsAsync(year, billPage, token); billTotal = page.Total; BillsGrid.ItemsSource = page.Rows; BillInfo.Text = $"第 {billPage} 页 · {page.Total} 条"; BillSummary.Text = "年度汇总：" + page.Summary; SelfFeedback.Text = "历史账单已刷新。"; }
    private async void BillsClick(object s, RoutedEventArgs e) => await Run(async t => { year = Year(); billPage = 1; await LoadPage(2, t); });
    private async void BillPrev(object s, RoutedEventArgs e) => await Run(async t => { if (billPage > 1) { billPage--; await Bills(t); } });
    private async void BillNext(object s, RoutedEventArgs e) => await Run(async t => { if (billPage * 10 < billTotal) { billPage++; await Bills(t); } });
    private async Task Overview(CancellationToken t) { var a = await client.OverviewAsync(t); OverviewText.Text = $"账号：{a.Account}   状态码：{a.State}   套餐：{a.Plan}\n平台原值：时长 {a.Minutes} · 流量 {a.Flow} · 余额 {a.Balance}（首页字段单位尚未确认，不与账单单位混用）"; SelfFeedback.Text = "账号概况已更新。"; }
    private async void OverviewClick(object s, RoutedEventArgs e) => await Run(Overview);
    private string? ExportPath(string filename)
    {
        var dialog = new SaveFileDialog { Title = "手动导出个人业务记录", Filter = "CSV 文件|*.csv", FileName = filename }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null; string path = Path.GetFullPath(dialog.FileName);
        if (path.StartsWith(store.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new SelfException("protocol"); return path;
    }
    private async Task Export<T>(string path, string[] headers, Func<int, CancellationToken, Task<Page<T>>> query, Func<T, string[]> columns, CancellationToken token)
    {
        await CsvExporter.WriteAsync(path, headers, query, columns, token, (count, total) => SelfFeedback.Text = $"正在导出 {count} / {total} 条…"); SelfFeedback.Text = "业务记录已导出到你选择的文件；配置清理不会删除它。";
    }
    private async void HistoryExportClick(object s, RoutedEventArgs e) => await Run(async t => { var dates = Dates(); string? path = ExportPath("NUFE-上网记录.csv"); if (path == null) { SelfFeedback.Text = "已取消。"; return; } var all = await CompleteHistory(dates.Start, dates.End, t); await Export(path, ["上线时间", "下线时间", "IP", "分钟", "MB", "金额/元", "国际上行MB", "国际下行MB", "国内上行MB", "国内下行MB", "NASIP", "NASPORT"], (p, c) => Task.FromResult(new Page<HistoryRow>(all.Total, all.Rows.Skip((p - 1) * 10).Take(10).ToArray(), all.Summary)), r => [r.LoginTime, r.LogoutTime, r.Ip, r.Minutes, r.FlowMb, r.Cost, r.InternetUp, r.InternetDown, r.ChinaUp, r.ChinaDown, r.NasIp, r.NasPort], t); });
    private async void BillExportClick(object s, RoutedEventArgs e) => await Run(async t => { int y = Year(); string? path = ExportPath("NUFE-年度账单.csv"); if (path == null) { SelfFeedback.Text = "已取消。"; return; } await Export(path, ["周期开始", "周期结束", "套餐", "月租/元", "计费/元", "分钟", "MB", "出账时间"], (p, c) => client.BillsAsync(y, p, c), r => [r.Start, r.End, r.Plan, r.BaseCost, r.ExtraCost, r.Minutes, r.FlowMb, r.Created], t); });
    private System.Windows.Controls.TextBox[] OperatorAccounts => [OperatorAccount0, OperatorAccount1, OperatorAccount2];
    private System.Windows.Controls.PasswordBox[] OperatorPasswords => [OperatorPassword0, OperatorPassword1, OperatorPassword2];
    private void ClearOperatorPasswords() { foreach (var box in OperatorPasswords) box.Clear(); }
    private async Task Operators(CancellationToken token) { var state = await client.OperatorsAsync(token); for (int i = 0; i < 3; i++) OperatorAccounts[i].Text = state.Groups[i].Account; ClearOperatorPasswords(); RecoveryReady.IsChecked = false; OperatorText.Text = string.Join("；", state.Groups.Select(g => g.Name + "：" + (g.Active ? "已绑定，密码未回填" : "未绑定"))); if (pendingWrite == "binding") pendingWrite = ""; SelfFeedback.Text = "绑定信息已刷新；密码不回填。"; }
    private async void OperatorsClick(object s, RoutedEventArgs e) => await Run(t => LoadPage(3, t));
    private async void BindClick(object s, RoutedEventArgs e) => await Run(async token =>
    {
        if (RecoveryReady.IsChecked != true) { SelfFeedback.Text = "请先确认已掌握恢复资料和网络影响。"; return; }
        var entries = OperatorAccounts.Select((box, i) => new SecretPair(box.Text.Trim(), OperatorPasswords[i].Password)).ToArray();
        try
        {
            var ticket = await client.PrepareOperatorFormAsync(entries, token);
            string preview = string.Join("\n", ticket.Before.Groups.Select((g, i) => g.Name + "：" + g.Display + " → " + (entries[i].Account.Length == 0 ? "未绑定（清空）" : "***" + entries[i].Account[Math.Max(0, entries[i].Account.Length - 4)..])));
            if (!await ConfirmAsync("运营商绑定确认", preview + "\n将提交三组完整表单，空行将解绑。可能影响上网；密码有效性需实际联网确认。\n目的地：10.100.0.3:8080，确认提交？")) { SelfFeedback.Text = "已取消，未提交。"; return; }
            pendingWrite = "binding"; await client.SubmitOperatorFormAsync(ticket, entries, token); await Operators(token); SelfFeedback.Text = "三组绑定状态已回查确认，未自动断网或认证。";
        }
        finally { ClearOperatorPasswords(); RecoveryReady.IsChecked = false; Array.Clear(entries); }
    }, true);
    private void CancelClick(object s, RoutedEventArgs e) => operation?.Cancel();

    private async Task Bound(CancellationToken t) { var page = await client.BoundDevicesAsync(boundPage, t); boundTotal = page.Total; BoundGrid.ItemsSource = page.Rows; BoundInfo.Text = $"第 {boundPage} 页 · 共 {page.Total} 条 · 绑定设备不是在线配额"; SelfFeedback.Text = page.Total == 0 ? "没有绑定设备。" : "绑定设备已加载。"; if (pendingWrite == "mac") pendingWrite = ""; }
    private async void BoundClick(object s, RoutedEventArgs e) => await Run(t => LoadPage(4, t));
    private async void BoundPrev(object s, RoutedEventArgs e) => await Run(async t => { if (boundPage > 1) { boundPage--; await Bound(t); } });
    private async void BoundNext(object s, RoutedEventArgs e) => await Run(async t => { if (boundPage * 10 < boundTotal) { boundPage++; await Bound(t); } });
    private async void RemoveMacClick(object s, RoutedEventArgs e) => await Run(async t =>
    {
        if (BoundGrid.SelectedItem is not BoundDevice device) { SelfFeedback.Text = "请选择一个绑定设备。"; return; }
        var ticket = await client.PrepareMacRemovalAsync(device, t);
        if (!await ConfirmAsync("解除 MAC 绑定", "解除此 MAC 绑定（与在线下线不同），可能影响后续认证。MAC：" + device.Mac + "。此接口尚未真实写入验收，确认继续？")) return;
        pendingWrite = "mac"; await client.RemoveMacAsync(ticket, t); await Bound(t); SelfFeedback.Text = "页面已确认移除绑定，未自动断网测试。";
    }, true);
}
