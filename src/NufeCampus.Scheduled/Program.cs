using System.Diagnostics;
using NufeCampus.Windows;

// Task Scheduler launches this windowless host, which waits for the console subtool.
// It accepts no arguments and never creates another schedule or a persistent process.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return 2;
        try
        {
            string? root = RuntimeScope.TestRoot();
            var settings = new LocalStore(root).ReadSettings();
            if (settings.KeepAliveStrategy != "scheduler" || !settings.Enabled || settings.ScheduledStopped) return 0;
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
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            try { await child.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } return 5; }
            await Task.WhenAll(output, error); return child.ExitCode;
        }
        catch { return 1; }
    }
}
