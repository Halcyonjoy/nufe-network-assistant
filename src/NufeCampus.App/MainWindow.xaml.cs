using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NufeCampus.Windows;
namespace NufeCampus.App;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly string? testRoot;
    private readonly string pipe;
    private SelfPage? selfPage;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource closed = new();
    private bool busy, refreshing, removed;
    private string AgentExe => Path.Combine(AppContext.BaseDirectory, "NufeCampus.Agent.exe");
    public MainWindow(LocalStore store, string? testRoot)
    {
        InitializeComponent(); this.store = store; this.testRoot = testRoot; pipe = LocalStore.PipeName + LocalStore.TestSuffix(testRoot);
        InitializeFeatures(); InitializeShell(); InitializeKeepAliveHistory();
        Closing += WindowClosing;
        if (testRoot != null) { Title = "NUFE Campus · 隔离测试"; ModeNote.Text = "隔离测试实例\n不访问校园网，不修改计划任务。"; }
        Loaded += async (_, _) =>
        {
            try
            {
                await LoadPreferences(); if (closed.IsCancellationRequested) return;
                await RefreshStatus(); if (closed.IsCancellationRequested) return;
                if (WindowState != WindowState.Minimized) refresh.Start();
                await StartSelfServiceAsync();
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        };
        refresh.Tick += async (_, _) => { if (!busy && !removed) await RefreshStatus(); };
        // Async handlers may still unwind after Closed; do not dispose their token source early.
        Closed += (_, _) => { refresh.Stop(); closed.Cancel(); CampusPassword.Clear(); sessionCredential = null; };
    }
    private async Task LoadPreferences()
    {
        loadingPreferences = true;
        try
        {
            var data = await Task.Run(() => (Settings: store.ReadSettings(), Campus: ReadOptional(false)));
            foregroundStrategy = data.Settings.KeepAliveStrategy; foregroundHasCredentials = data.Campus != null;
            EnableBox.IsChecked = data.Settings.Enabled; AutoStartBox.IsChecked = data.Settings.AutoStart; HttpConsent.IsChecked = data.Settings.HttpConsent;
            StrategyBox.SelectedIndex = data.Settings.KeepAliveStrategy == "scheduler" ? 1 : 0;
            CheckIntervalBox.Text = data.Settings.CheckIntervalSeconds.ToString(); UpdateStrategyControls();
            TaskNameBox.Text = data.Settings.TaskName;
            taskNameUnsaved = false;
            ForegroundQueryBox.Text = data.Settings.ForegroundQuerySeconds.ToString();
            ForegroundProbeBox.Text = data.Settings.ForegroundProbeSeconds.ToString(); ApplyObservationSettings(data.Settings);
            CarrierBox.SelectedIndex = data.Settings.Carrier == "@cmcc" ? 0 : 1;
            AccountBox.Text = data.Campus?.Account ?? "";
            CredentialNote.Text = data.Campus != null ? "校园网凭据已加密保存。密码不会回填到界面。" : "尚未保存可用的校园网凭据。";
            LoadFeaturePreferences(data.Settings); InitializeCredentialChoice(data.Campus != null); UpdateConfigurationSummary();
            if (testRoot == null) { using var timeout = new CancellationTokenSource(5000); bool exists = await StartupTask.ExistsAsync(timeout.Token, taskName: data.Settings.TaskName); ShowTaskLocation(data.Settings, exists); }
            else TaskNote.Text = "隔离测试不注册 Windows 计划任务。";
        }
        catch { Feedback.Text = "读取配置失败。请检查本机文件；损坏的凭据需要重新输入，不会采用明文回退。"; }
        finally { loadingPreferences = false; }
    }
    private SecretPair? ReadOptional(bool broad)
    {
        try { return store.ReadSecret(broad); }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException) { return null; }
    }
    private async Task<AgentResponse> Call(string command, int seconds = 5)
    {
        if (UsesScheduledKeepAlive) return await ScheduledCommand(command, seconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return await AgentPipe.CallAsync(pipe, command, timeout.Token);
    }
    private async Task RefreshStatus()
    {
        if (refreshing || removed || closed.IsCancellationRequested) return;
        refreshing = true;
        try
        {
            var reply = await ReadForegroundStatus();
            if (closed.IsCancellationRequested) return;
            PhaseText.Text = Texts.Phase(reply.Status.Phase); IssueText.Text = Texts.Issue(reply.Status.Issue);
            RenderFeatureStatus(reply.Status);
            KeeperText.Text = reply.Status.Paused ? "保活：已暂停，跨重启保留" : reply.Status.KeepAliveEnabled ? "保活：已启用，按网络状态自动检查" : "保活：已关闭";
            RecoveryText.Text = reply.Status.LastRecovery is { } time ? "最近恢复：" + time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss") : "最近恢复：尚无记录";
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or JsonException)
        {
            if (!closed.IsCancellationRequested)
            {
                var unknown = new AgentStatus { Phase = "unreachable", Issue = "network", HasCredentials = foregroundHasCredentials, CheckedAt = DateTimeOffset.UtcNow };
                PhaseText.Text = "实时状态暂不可用"; IssueText.Text = "稍后自动重试；未启动保活或提交认证。";
                RenderFeatureStatus(unknown); KeeperText.Text = "保活：状态尚未确认";
            }
        }
        finally { refreshing = false; }
    }
    private async Task EnsureAgent()
    {
        if (UsesScheduledKeepAlive)
        {
            if (sessionCredential != null || !store.HasSecret()) throw new UserNotice("请先加密保存账号密码。");
            return;
        }
        if (sessionCredential != null || !store.HasSecret())
        {
            NavigateFromShortcut("settings");
            throw new UserNotice("后台保活需要加密保存账号密码。请在账号页勾选加密保存并应用，或仅在当前窗口手动认证。");
        }
        try { await Call("status"); return; } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        if (AgentLifetime.IsRunning(testRoot)) throw new UserNotice("后台正在运行但未响应，请稍后重试；不会重复启动。");
        if (!File.Exists(AgentExe)) throw new UserNotice("未找到后台程序，请从完整发布目录运行配置界面。");
        var start = new ProcessStartInfo(AgentExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        if (testRoot != null) { start.ArgumentList.Add("--test-root"); start.ArgumentList.Add(testRoot); }
        using var launched = Process.Start(start) ?? throw new UserNotice("后台无法启动。");
        for (int i = 0; i < 20; i++) { await Task.Delay(150, closed.Token); try { await Call("status", 2); return; } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { if (launched.HasExited) break; } }
        throw new UserNotice("后台没有就绪，请检查发布目录和脱敏诊断。");
    }
    private async Task Run(Func<Task> operation)
    {
        if (busy || removed || closed.IsCancellationRequested) return;
        busy = true; SaveButton.IsEnabled = AccountApplyButton.IsEnabled = false; MainConnectionButton.IsEnabled = false; ConnectionActions.IsEnabled = false; Feedback.Text = "正在处理…";
        try { await operation(); }
        catch (UserNotice notice) { Feedback.Text = notice.Message; }
        catch (OperationCanceledException) { if (!closed.IsCancellationRequested) Feedback.Text = "本次操作超时或取消，请先刷新状态，不要重复提交认证。"; }
        catch { Feedback.Text = "本次操作未完成。请检查配置、凭据或后台状态；不会自动重复提交。"; }
        finally { busy = false; if (!closed.IsCancellationRequested) { SaveButton.IsEnabled = AccountApplyButton.IsEnabled = !removed; ConnectionActions.IsEnabled = !removed; RenderShellStatus(latestStatus); } }
    }
    private async Task<AgentResponse?> AgentForConfiguration()
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); limit.CancelAfter(1000);
            return await AgentPipe.CallAsync(pipe, "status", limit.Token);
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
        {
            if (AgentLifetime.IsRunning(testRoot)) throw new UserNotice("后台仍在运行但未响应，本次不会更改账号或凭据。请稍后重试，或先退出后台。");
            return null;
        }
    }
    private async void SaveClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (selfPage?.IsWorking == true) throw new UserNotice("请等待或取消自助请求，再保存或切换账号。");
        string account = AccountBox.Text.Trim(), password = CampusPassword.Password;
        bool enabled = EnableBox.IsChecked == true, autoStart = AutoStartBox.IsChecked == true, consent = HttpConsent.IsChecked == true;
        string strategy = StrategyBox.SelectedIndex == 1 ? "scheduler" : "agent";
        var observation = ReadObservationInputs();
        if (!int.TryParse(CheckIntervalBox.Text, out int interval) || !KeepAliveConfiguration.IsValid(strategy, interval))
            throw new UserNotice(strategy == "scheduler" ? "任务计划间隔须为 60–86400 秒，且为整分钟。" : "后台检查间隔须为 10–86400 秒。");
        if (strategy == "scheduler") autoStart = false;
        string carrier = CarrierBox.SelectedIndex == 0 ? "@cmcc" : "@njxy";
        if (!System.Text.RegularExpressions.Regex.IsMatch(account, @"^\d{6,16}$")) throw new UserNotice("请填写不带运营商后缀的学号。");
        if (RememberCampus.IsChecked != true) { await ApplySessionCredential(account, password, carrier); return; }
        var featurePreferences = ReadFeaturePreferences();
        if (enabled && !consent) throw new UserNotice("启用认证前，请在“设置 → 运行偏好”中阅读并确认校方 HTTP 传输说明。");
        SecretPair? campusToSave = null; Settings preferences = new(); bool previouslyPaused = false;
        await Task.Run(() =>
        {
            var old = password.Length == 0 ? (sessionCredential?.Account == account ? sessionCredential : ReadOptional(false)) : null;
            if (password.Length == 0 && (old == null || old.Account != account)) throw new UserNotice("请填写校园网密码；更换学号时不能沿用旧密码。");
            var prior = store.ReadSecret(); bool same = prior?.Account == account;
            if (!same && store.Accounts().Any(p => p.Account == account)) throw new UserNotice("该学号已经保存，请先载入该账号，再修改密码或设备规则。");
            var settings = store.ReadSettings();
            previouslyPaused = settings.Memory.PausedByTool;
            bool repaired = password.Length != 0 || settings.Carrier != carrier;
            preferences = settings with
            {
                Enabled = enabled,
                AutoStart = autoStart,
                HttpConsent = consent,
                Carrier = carrier,
                KeepAliveStrategy = strategy,
                CheckIntervalSeconds = interval,
                ScheduledStopped = false,
                ForegroundQuerySeconds = observation.Query,
                ForegroundProbeSeconds = observation.Probe,
                SelfConsent = featurePreferences.SelfConsent,
                AutoOfflineEnabled = same && featurePreferences.AutoOfflineEnabled,
                SelfLoginBlocked = same && !repaired && settings.SelfLoginBlocked,
                ProtectedMacs = same ? featurePreferences.ProtectedMacs : [],
                OfflineOrder = same ? featurePreferences.OfflineOrder : "oldest",
                Memory = same ? settings.Memory with { CredentialsRejected = repaired ? false : settings.Memory.CredentialsRejected } : new(PausedByTool: prior != null),
                CredentialIssue = same && !repaired ? settings.CredentialIssue : "",
                LastNotifiedIssue = same && !repaired ? settings.LastNotifiedIssue : ""
            };
            campusToSave = new(account, password.Length > 0 ? password : old!.Password);
        });
        AgentResponse? running = await AgentForConfiguration();
        if (running != null && running.Status.ProtocolVersion < 2) throw new UserNotice("旧版保活后台仍在运行。本预览不写入新配置或替换它，请在方便时手动更新后台后再保存。");
        if (running != null) await StopAgent();
        using (var configurationLimit = CancellationTokenSource.CreateLinkedTokenSource(closed.Token))
        {
            configurationLimit.CancelAfter(TimeSpan.FromSeconds(65));
            using var lease = await RunnerLease.AcquireAsync(store, configurationLimit.Token);
            var latest = store.ReadSettings();
            if (store.ReadSecret()?.Account == account && password.Length == 0 && latest.Carrier == carrier)
            {
                previouslyPaused = latest.Memory.PausedByTool;
                preferences = preferences with { Memory = latest.Memory, CredentialIssue = latest.CredentialIssue, LastNotifiedIssue = latest.LastNotifiedIssue };
            }
            await Task.Run(() => { store.SaveProfile(campusToSave!, carrier, preferences, previouslyPaused); });
            store.SaveLastCheck(new() { Phase = "waiting" });
            try { await ConfigureKeepAliveTask(store.ReadSettings()); }
            catch { throw new UserNotice("配置已保存，但 Windows 任务设置未完成。请检查后重新保存应用。"); }
        }
        sessionCredential = null; WelcomeCard.Visibility = Visibility.Collapsed;
        CampusPassword.Clear(); CredentialNote.Text = "校园网凭据已加密保存，密码不会回填。";
        ResetSelfPage(); LoadFeaturePreferences(store.ReadSettings());
        if (strategy == "agent" && (running != null || enabled || autoStart))
        {
            await EnsureAgent(); var reply = await Call("reload");
            if (!reply.Ok) throw new UserNotice("配置已保存，后台重载未确认，请刷新状态。");
        }
        Feedback.Text = strategy == "scheduler" && enabled ? $"已加密保存并配置任务计划，每 {interval / 60} 分钟检查，不运行常驻后台。" : enabled || autoStart ? "已加密保存并应用，后台按运行偏好工作。" : "账号密码已加密保存。保活和登录自启动未开启；需要时可在运行偏好中启用。";
        if (testRoot != null) Feedback.Text += "（隔离测试，未访问校园网或修改真实计划任务）";
        ApplyObservationSettings(store.ReadSettings()); observationUnsaved = false;
        ObservationNote.Text = "前台刷新设置已应用。";
        SettingsSaved(); await RefreshStatus();
    });
    private async void StartClick(object sender, RoutedEventArgs e) => await Run(async () => { if (UsesScheduledKeepAlive) await StartScheduledKeepAlive(); else { await EnsureAgent(); Feedback.Text = "后台已启动。"; } await RefreshStatus(); });
    private async void RefreshClick(object sender, RoutedEventArgs e) { nextForegroundProbe = default; await RefreshForegroundInternet(); await RefreshStatus(); }
    private async Task Send(string command) => await Run(async () =>
    {
        await EnsureAgent(); var reply = await Call(command, command == "logout" ? 20 : 5);
        Feedback.Text = reply.Ok ? "已应用操作。" : command == "logout" ? "注销结果尚未确认，保活已持久暂停。请核对官方页面。" : "后台操作未确认。";
        await RefreshStatus();
    });
    private async void PauseClick(object sender, RoutedEventArgs e) => await Send("pause");
    private async void ResumeClick(object sender, RoutedEventArgs e) => await Send("resume");
    private async void RetryClick(object sender, RoutedEventArgs e) => await Send("retry");
    private async void LogoutClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("本机下线", "将下线当前电脑并持久暂停保活，直到你手动恢复。确认现在断开本机认证？")) await Send("logout");
    }
    private async void ReauthenticateClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("手动重新认证", "将短暂下线本机，再恢复保活并尝试认证。失败时会保留暂停或错误状态，需要你处理。确认现在重新认证？")) return;
        await Run(async () =>
        {
            await EnsureAgent(); var status = await Call("status");
            if (status.Status.ProtocolVersion < 2) throw new UserNotice("当前运行的是旧后台，本次不会重启或替换它；新重新认证流程需要新版后台。");
            var result = await Call("reauthenticate", 60); Feedback.Text = result.Ok ? "重新认证已确认，保活已恢复。" : "重新认证未完成，暂停或错误状态已保留。"; await RefreshStatus();
        });
    }
    private async Task StopAgent()
    {
        if (UsesScheduledKeepAlive) await StopScheduledKeepAlive();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(12));
        try { await AgentLifetime.StopAsync(testRoot, AgentExe, limit.Token); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            throw new UserNotice("未能确认后台已退出，请稍后重试。不会宣称退出成功或继续清理数据。");
        }
    }
    private async void StopClick(object sender, RoutedEventArgs e) => await Run(async () => { await StopAgent(); Feedback.Text = UsesScheduledKeepAlive ? "任务计划保活已停止，重新启动或保存应用后恢复。" : "本次后台已退出；下次 Windows 登录仍按自启动偏好运行。"; await RefreshStatus(); });
    private async void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("移除本机数据", "移除当前用户的自启动任务、本机配置、加密凭据和诊断日志。手动导出的备份和记录不删除。程序文件可随后自行删除。确认清理？")) return;
        await Run(async () =>
        {
            await StopAgent(); if (testRoot == null) { using var limit = new CancellationTokenSource(10000); await StartupTask.SetAsync(false, AgentExe, limit.Token, taskName: store.ReadSettings().TaskName); }
            await Task.Run(store.RemoveOwnedData); removed = true; refresh.Stop(); CampusPassword.Clear();
            Feedback.Text = "本机配置、凭据和自启动已清理。手动导出文件保留，可关闭窗口。"; PhaseText.Text = "本机数据已移除"; IssueText.Text = "";
        });
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void OpenSelfClick(object sender, RoutedEventArgs e) => OpenUrl("http://10.100.0.3:8080/Self/");
    private void OpenNativeSelfClick(object sender, RoutedEventArgs e)
    {
        NavigateFromShortcut("devices");
    }
    private void OpenPortalClick(object sender, RoutedEventArgs e) => OpenUrl("http://10.200.253.5/");
    private string? ExportPath(string title, string filter, string filename)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = filename, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return null;
        string path = Path.GetFullPath(dialog.FileName);
        if (path.Equals(store.Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(store.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new UserNotice("请选择工具数据目录以外的位置，以免备份随清理受影响。");
        return path;
    }
    private async void BackupClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        string? path = ExportPath("导出不含凭据的备份", "JSON 文件|*.json", "NUFE-偏好备份.json"); if (path == null) { Feedback.Text = "已取消。"; return; }
        var settings = store.ReadSettings(); var backup = new PreferencesBackup(1, settings.Enabled, settings.AutoStart, settings.Carrier, settings.KeepAliveStrategy, settings.CheckIntervalSeconds, settings.ForegroundQuerySeconds, settings.ForegroundProbeSeconds);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(backup, AppJson.Default.PreferencesBackup), closed.Token); Feedback.Text = "偏好已导出，文件不包含任何凭据。";
    });
    private async void ImportClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "JSON 备份|*.json", Title = "导入不含凭据的偏好" }; if (dialog.ShowDialog(this) != true) { Feedback.Text = "已取消。"; return; }
        if (new FileInfo(dialog.FileName).Length > 65536) throw new UserNotice("备份文件过大。");
        var backup = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(dialog.FileName, closed.Token), AppJson.Default.PreferencesBackup);
        if (backup == null || backup.Version != 1 || backup.Carrier is not ("@cmcc" or "@njxy") || !KeepAliveConfiguration.IsValid(backup.KeepAliveStrategy, backup.CheckIntervalSeconds) || !KeepAliveConfiguration.ValidObservation(backup.ForegroundQuerySeconds, backup.ForegroundProbeSeconds)) throw new UserNotice("备份版本或内容不支持。");
        EnableBox.IsChecked = backup.Enabled; AutoStartBox.IsChecked = backup.AutoStart; CarrierBox.SelectedIndex = backup.Carrier == "@cmcc" ? 0 : 1;
        StrategyBox.SelectedIndex = backup.KeepAliveStrategy == "scheduler" ? 1 : 0; CheckIntervalBox.Text = backup.CheckIntervalSeconds.ToString(); UpdateStrategyControls();
        ForegroundQueryBox.Text = backup.ForegroundQuerySeconds.ToString(); ForegroundProbeBox.Text = backup.ForegroundProbeSeconds.ToString();
        Feedback.Text = "偏好已载入，请检查后保存并应用。凭据及 HTTP 确认不会从备份导入。";
    });
    private async void DiagnosticClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        string? path = ExportPath("导出脱敏诊断", "ZIP 文件|*.zip", "NUFE-脱敏诊断.zip"); if (path == null) { Feedback.Text = "已取消。"; return; }
        AgentStatus snapshot; try { snapshot = (await Call("status")).Status; } catch { snapshot = new() { Phase = "stopped" }; }
        await Task.Run(() =>
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write); using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using (var output = archive.CreateEntry("status.json").Open()) JsonSerializer.Serialize(output, snapshot with { CurrentAccount = "", ServerMessage = "" }, AppJson.Default.AgentStatus);
            using var text = new StreamWriter(archive.CreateEntry("events.log").Open(), Encoding.UTF8);
            if (Directory.Exists(store.Logs)) foreach (var log in Directory.EnumerateFiles(store.Logs, "*.log").Select(p => new FileInfo(p)).Where(f => f.Length <= 1024 * 1024).OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
            {
                foreach (string line in File.ReadLines(log.FullName))
                {
                    string[] parts = line.Split(' ', 2); if (parts.Length == 2 && DateTimeOffset.TryParse(parts[0], out var time) && Enum.TryParse<DiagnosticCode>(parts[1], out var code) && Enum.IsDefined(code)) text.WriteLine(time.ToString("O") + " " + code);
                }
            }
        }); Feedback.Text = "脱敏诊断已导出，不含账号、IP、MAC、密码或令牌。";
    });
    private sealed class UserNotice(string message) : Exception(message);
}
