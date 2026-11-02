using System.Diagnostics;

namespace NufeCampus.Windows;

public static class QuotaWorker
{
    public static async Task<string> RunAsync(CancellationToken token)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "NufeCampus.SelfWorker.exe");
        if (!File.Exists(exe)) return "unavailable";
        using var child = Process.Start(new ProcessStartInfo(exe, "--quota") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
        if (child == null) return "unavailable";
        try
        {
            await child.WaitForExitAsync(token);
            return child.ExitCode switch { 0 => "freed", 3 => "no_candidate", 4 => "captcha", 5 => "uncertain", 6 => "reconnected", 7 => "self_login_rejected", _ => "stopped" };
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            throw;
        }
    }
}
