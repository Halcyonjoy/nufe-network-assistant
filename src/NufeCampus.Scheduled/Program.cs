using System.Diagnostics;
using System.Text.Json;
using NufeCampus.Windows;

// Task Scheduler launches this windowless host, which waits for the console subtool.
// It accepts no arguments and never creates another schedule or a persistent process.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return 2;
        LocalStore? store = null;
        var history = new KeepAliveRunBuilder(KeepAliveSource.Scheduler);
        KeepAliveRun? result = null;
        try
        {
            string? root = RuntimeScope.TestRoot();
            store = new LocalStore(root);
            var settings = store.ReadSettings();
            if (settings.KeepAliveStrategy != "scheduler") { history.Step("strategy_changed"); history.Observe("disabled", "strategy_changed"); return 0; }
            if (!settings.Enabled || settings.ScheduledStopped) { history.Observe("disabled", settings.ScheduledStopped ? "stopped" : ""); return 0; }
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NufeCampus.Cli.exe"), "--authenticate")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["NUFE_CAMPUS_SCHEDULED"] = "1";
            if (root != null) start.Environment["NUFE_CAMPUS_TEST_ROOT"] = root;
            using var child = Process.Start(start); if (child == null) return 1;
            history.Step("process_started");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            try { await child.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } history.Observe("timeout"); return 5; }
            await Task.WhenAll(output, error);
            try
            {
                var response = JsonSerializer.Deserialize(await output, AppJson.Default.AgentResponse);
                if (response?.Run is { } run && KeepAliveHistoryCodes.Valid(run) && run.Source == KeepAliveSource.Scheduler)
                    result = run with
                    {
                        StartedAt = history.Finish().StartedAt,
                        FinishedAt = DateTimeOffset.UtcNow,
                        Steps = new[] { "started", "process_started" }.Concat(run.Steps.Where(s => s != "started")).Append("process_completed").Take(32).ToArray()
                    };
                else if (response != null)
                {
                    history.Step("unexpected_error"); history.Observe("unconfirmed", "process_failed");
                }
            }
            catch (JsonException) { }
            if (result == null && child.ExitCode != 0) history.Observe(child.ExitCode == 4 ? "busy" : child.ExitCode == 5 ? "timeout" : "unconfirmed", "process_failed");
            history.Step("process_completed");
            return child.ExitCode;
        }
        catch { history.Step("unexpected_error"); history.Observe("unconfirmed", "configuration_error"); return 1; }
        finally { store?.RecordKeepAliveRun(result ?? history.Finish()); }
    }
}
