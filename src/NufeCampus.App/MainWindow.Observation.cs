using System.Net.Http;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private DateTimeOffset nextForegroundProbe;
    private string foregroundInternet = "unknown";
    private int foregroundProbeSeconds = 120, foregroundNetworkVersion;
    private bool probingForeground, observationUnsaved;

    private (int Query, int Probe) ReadObservationInputs()
    {
        if (!int.TryParse(ForegroundQueryBox.Text, out int query) || !int.TryParse(ForegroundProbeBox.Text, out int probe) ||
            !KeepAliveConfiguration.ValidObservation(query, probe))
            throw new UserNotice("前台认证查询间隔须为 1–3600 秒，外网探测间隔须为 5–86400 秒。请填写整数。");
        return (query, probe);
    }

    private void ApplyObservationSettings(Settings settings)
    {
        refresh.Interval = TimeSpan.FromSeconds(settings.ForegroundQuerySeconds);
        foregroundProbeSeconds = settings.ForegroundProbeSeconds;
        nextForegroundProbe = default;
    }

    private void ObservationInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || loadingPreferences) return;
        observationUnsaved = true;
        ObservationNote.Text = "前台刷新间隔尚未应用。";
    }

    private async void ApplyObservationClick(object sender, System.Windows.RoutedEventArgs e) => await Run(async () =>
    {
        var values = ReadObservationInputs();
        bool firstPreferences = !System.IO.File.Exists(System.IO.Path.Combine(store.Root, "settings.json"));
        var settings = store.UpdateSettings(s => s with
        {
            ForegroundQuerySeconds = values.Query,
            ForegroundProbeSeconds = values.Probe,
            Enabled = !firstPreferences && s.Enabled,
            AutoStart = !firstPreferences && s.AutoStart
        });
        ApplyObservationSettings(settings);
        observationUnsaved = false;
        ObservationNote.Text = $"已应用：认证查询 {values.Query} 秒，外网探测 {values.Probe} 秒。";
        Feedback.Text = "前台刷新设置已保存；保活策略和账号保持原设置。";
        await RefreshStatus();
    });

    // Called by the independent one-second network sampler, not by the authentication timer.
    private async Task RefreshForegroundInternet()
    {
        if (probingForeground || closed.IsCancellationRequested || removed || WindowState == System.Windows.WindowState.Minimized ||
            DateTimeOffset.UtcNow < nextForegroundProbe) return;
        probingForeground = true;
        int networkVersion = foregroundNetworkVersion;
        nextForegroundProbe = DateTimeOffset.UtcNow.AddSeconds(foregroundProbeSeconds);
        try
        {
            string result = "unknown";
            if (testRoot == null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var probe = new InternetProbe();
                result = await probe.CheckAsync(timeout.Token) ? "reachable" : "probe_failed";
            }
            if (closed.IsCancellationRequested || WindowState == System.Windows.WindowState.Minimized || networkVersion != foregroundNetworkVersion) return;
            foregroundInternet = result;
            InternetUpdatedText.Text = "外网探测 · " + DateTimeOffset.Now.ToString("HH:mm:ss") + (testRoot == null ? "" : "（隔离未联网）");
            if (latestStatus != null) RenderFeatureStatus(latestStatus with { Internet = result });
        }
        catch (Exception error) when (error is System.IO.IOException or HttpRequestException or OperationCanceledException) { }
        finally { probingForeground = false; }
    }

    // Observation never takes the writer lease, calls EnsureAgent, or executes an engine command.
    // A stopped keeper and a busy authentication writer must not prevent read-only observation.
    private async Task<AgentResponse> ReadForegroundStatus()
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        using var portal = new CampusPortal();
        var reply = await Task.Run(() => OneShotAuthentication.QueryAsync(store, portal,
            testRoot == null ? Wifi.Read : () => new("off_campus"), limit.Token), limit.Token);
        var settings = store.ReadSettings();
        bool active = settings.KeepAliveStrategy == "scheduler"
            ? !settings.ScheduledStopped : AgentLifetime.IsRunning(testRoot);
        AgentStatus? keeper = null;
        if (settings.KeepAliveStrategy == "scheduler")
        {
            try { keeper = store.ReadLastCheck(); }
            catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException) { }
        }
        if (settings.KeepAliveStrategy == "agent" && active)
        {
            using var metadataLimit = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            metadataLimit.CancelAfter(500);
            try { keeper = (await AgentPipe.CallAsync(pipe, "status", metadataLimit.Token)).Status; }
            catch (Exception error) when (error is System.IO.IOException or TimeoutException or OperationCanceledException) { }
        }
        return reply with
        {
            Status = reply.Status with
            {
                KeepAliveEnabled = settings.Enabled && active && sessionCredential == null,
                Paused = settings.Memory.PausedByTool,
                Internet = foregroundInternet,
                LastRecovery = keeper?.LastRecovery,
                FailureAt = keeper?.FailureAt,
                ServerMessage = keeper?.ServerMessage ?? "",
                Issue = reply.Status.Issue.Length > 0 ? reply.Status.Issue : settings.CredentialIssue
            }
        };
    }
}
