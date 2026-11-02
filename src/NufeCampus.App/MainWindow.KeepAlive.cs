using System.IO;
using System.Windows;
using System.Windows.Controls;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private bool taskNameUnsaved;
    private void ShowTaskLocation(Settings settings, bool exists)
    {
        string name = StartupTask.ScopedName(taskName: settings.TaskName);
        TaskNote.Text = $"任务计划程序库（根目录） → {name}\n" + (exists ? "任务已注册；列表未更新时按 F5 刷新。" : "任务尚未注册；启用对应策略并保存应用后创建。");
    }

    private void TaskNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || loadingPreferences) return;
        taskNameUnsaved = true;
        TaskNameNote.Text = "名称尚未应用，请点击“应用计划名称”；其他偏好仍按原方式保存。";
    }

    private async void ApplyTaskNameClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        string name = TaskNameBox.Text.Trim();
        if (!StartupTask.ValidName(name)) throw new UserNotice("计划名称最多 128 个字符，不能包含路径分隔符、控制字符或 \\ / : * ? \" < > |，也不能为 . 或 ..。");
        var running = await AgentForConfiguration();
        if (running != null)
        {
            using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await AgentLifetime.StopAsync(testRoot, AgentExe, stopLimit.Token);
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); limit.CancelAfter(TimeSpan.FromSeconds(65));
            using var lease = await RunnerLease.AcquireAsync(store, limit.Token);
            if (testRoot == null) await StartupTask.RenameAsync(store, name, AgentExe, limit.Token);
            else
            {
                // UI tests never configure real tasks; first-use naming must not enable keepalive.
                bool firstPreferences = !File.Exists(Path.Combine(store.Root, "settings.json"));
                store.UpdateSettings(s => s with { TaskName = name, Enabled = !firstPreferences && s.Enabled, AutoStart = !firstPreferences && s.AutoStart });
            }
            var settings = store.ReadSettings();
            TaskNameBox.Text = settings.TaskName;
            taskNameUnsaved = false;
            bool exists = testRoot == null && await StartupTask.ExistsAsync(limit.Token, taskName: settings.TaskName);
            ShowTaskLocation(settings, exists);
            TaskNameNote.Text = "计划名称已应用；间隔、启用和暂停状态保持原设置。";
            Feedback.Text = "计划名称已应用。";
        }
        catch (IOException)
        {
            throw new UserNotice("计划名称未能应用：名称可能已被占用，或 Windows 任务迁移失败。请检查任务列表后重试；不会覆盖其他任务。");
        }
        finally { if (running != null && !closed.IsCancellationRequested) await EnsureAgent(); }
    });

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
            scheduled ? ScheduledExe : AgentExe, timeout.Token, strategy: settings.KeepAliveStrategy, intervalSeconds: settings.CheckIntervalSeconds, taskName: settings.TaskName);
        ShowTaskLocation(settings, scheduled ? settings.Enabled && !settings.ScheduledStopped : settings.AutoStart);
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
