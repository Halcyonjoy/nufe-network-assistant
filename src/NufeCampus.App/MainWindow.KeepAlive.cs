using System.IO;
using System.Windows;
using System.Windows.Controls;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private bool UsesScheduledKeepAlive => store.ReadSettings().KeepAliveStrategy == "scheduler";
    private string ScheduledExe => Path.Combine(AppContext.BaseDirectory, "NufeCampus.Scheduled.exe");

    private void StrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingPreferences) return;
        if (StrategyBox.SelectedIndex == 1 && (!int.TryParse(CheckIntervalBox.Text, out int value) || value < 60 || value % 60 != 0))
            CheckIntervalBox.Text = "60";
        UpdateStrategyControls(); MarkUnsaved();
    }

    private void UpdateStrategyControls()
    {
        bool scheduled = StrategyBox.SelectedIndex == 1;
        AutoStartBox.IsEnabled = RememberCampus.IsChecked == true && !scheduled;
        StrategyNote.Text = scheduled
            ? "任务计划：60–86400 秒，须为整分钟。每次启动子工具，检查后退出；不常驻、不唤醒电脑，仅在当前 Windows 用户已登录时运行。网络变化等待下次计划检查。"
            : "常驻后台：10–86400 秒。网络变化和正常唤醒时也会立即检查；不主动唤醒电脑。登录启动可单独选择。";
    }

    private async Task ConfigureKeepAliveTask(Settings settings)
    {
        if (testRoot != null) { TaskNote.Text = "隔离测试不配置真实 Windows 计划任务。"; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        bool scheduled = settings.KeepAliveStrategy == "scheduler";
        await StartupTask.SetAsync(scheduled ? settings.Enabled && !settings.ScheduledStopped : settings.AutoStart,
            scheduled ? ScheduledExe : AgentExe, timeout.Token, strategy: settings.KeepAliveStrategy, intervalSeconds: settings.CheckIntervalSeconds);
        TaskNote.Text = scheduled
            ? settings.Enabled && !settings.ScheduledStopped ? $"任务计划已配置，每 {settings.CheckIntervalSeconds / 60} 分钟检查。" : "任务计划保活已停止。"
            : settings.AutoStart ? "登录启动任务已配置。" : "登录启动任务已移除。";
    }

    private async Task<AgentResponse> ScheduledCommand(string command, int seconds)
    {
        if (command == "status")
        {
            var settings = store.ReadSettings();
            var snapshot = store.ReadLastCheck() ?? new AgentStatus { Phase = "waiting" };
            bool stale = snapshot.CheckedAt == default || DateTimeOffset.UtcNow - snapshot.CheckedAt > TimeSpan.FromSeconds(Math.Max(120, settings.CheckIntervalSeconds * 2));
            return new(true, snapshot with
            {
                ProtocolVersion = 2,
                ProcessId = 0,
                KeepAliveEnabled = settings.Enabled && !settings.ScheduledStopped,
                HasCredentials = store.HasSecret(),
                Paused = settings.Memory.PausedByTool,
                Phase = settings.Memory.PausedByTool ? "paused" : !settings.Enabled || settings.ScheduledStopped ? "disabled" : stale ? "waiting" : snapshot.Phase,
                Authentication = stale ? "unknown" : snapshot.Authentication,
                CurrentAccount = ""
            });
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(65, seconds)));
        using var lease = await RunnerLease.AcquireAsync(store, timeout.Token);
        using var portal = new CampusPortal();
        Func<WifiState> network = testRoot == null ? Wifi.Read : () => new("off_campus");
        var engine = new KeepAliveEngine(store, portal, network, _ => { }, quotaRecovery: testRoot == null ? QuotaWorker.RunAsync : null);
        if (command == "query") return await OneShotAuthentication.QueryAsync(store, portal, network, timeout.Token);
        var result = await engine.CommandAsync(command, timeout.Token);
        if ((command is "resume" or "retry" or "authenticate_resume") && result.Ok)
        {
            store.UpdateSettings(s => s with { ScheduledStopped = false });
            await ConfigureKeepAliveTask(store.ReadSettings());
            await engine.TickAsync(false, timeout.Token);
            result = result with { Status = engine.Status };
        }
        store.SaveLastCheck(result.Status);
        return result;
    }

    private async Task StartScheduledKeepAlive()
    {
        if (!store.HasSecret()) throw new UserNotice("任务计划保活需要先加密保存账号密码。");
        if (!store.ReadSettings().Enabled) throw new UserNotice("请先在运行偏好中启用保活并保存应用。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        using var lease = await RunnerLease.AcquireAsync(store, timeout.Token);
        store.UpdateSettings(s => s with { ScheduledStopped = false });
        await ConfigureKeepAliveTask(store.ReadSettings());
        Feedback.Text = "任务计划保活已启动，等待下次计划检查；已有暂停状态会保留。";
    }

    private async Task StopScheduledKeepAlive()
    {
        // Persist the stop before removing the trigger, so a queued invocation also stays silent.
        store.UpdateSettings(s => s with { ScheduledStopped = true });
        await ConfigureKeepAliveTask(store.ReadSettings());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        using var lease = await RunnerLease.AcquireAsync(store, timeout.Token);
    }
}
