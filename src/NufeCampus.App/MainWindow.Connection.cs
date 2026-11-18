using System.Diagnostics;
using System.IO;
using System.Windows;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private string AgentExe => Path.Combine(AppContext.BaseDirectory, "NufeCampus.Agent.exe");
    private async Task<AgentResponse> Call(string command, int seconds = 5)
    {
        if (UsesScheduledKeepAlive) return await ScheduledCommand(command, seconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return await AgentPipe.CallAsync(pipe, command, timeout.Token);
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
    private async void ReauthenticateClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("手动重新认证", "将短暂下线本机，再恢复保活并尝试认证。失败时会保留暂停或错误状态，需要你处理。确认现在重新认证？")) return;
        await Run(async () =>
        {
            await EnsureAgent(); var status = await Call("status");
            if (status.Status.ProtocolVersion < 2) throw new UserNotice("当前后台版本过旧，请先更新并重新启动后台，再进行手动重新认证。");
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
}
