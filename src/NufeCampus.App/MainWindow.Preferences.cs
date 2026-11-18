using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
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
        if (running != null && running.Status.ProtocolVersion < 2) throw new UserNotice("旧版保活后台仍在运行，请先更新并重新启动后台，再保存配置。");
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
}
