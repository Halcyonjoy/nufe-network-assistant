using System.Diagnostics;
using System.Xml.Linq;
namespace NufeCampus.Windows;

public static class StartupTask
{
    public static string Name => "NufeCampus-" + LocalStore.UserId;
    private static string ScopedName(string? testId)
    {
        if (testId == null) return Name;
        if (!System.Text.RegularExpressions.Regex.IsMatch(testId, "^[0-9a-f]{32}$")) throw new ArgumentException("Invalid task test id");
        return Name + "-test-" + testId;
    }
    public static string Xml(string executable, bool triggerEnabled = true, string strategy = "agent", int intervalSeconds = 30)
    {
        if (!KeepAliveConfiguration.IsValid(strategy, intervalSeconds)) throw new ArgumentException("Invalid task interval");
        executable = Path.GetFullPath(executable);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var trigger = strategy == "scheduler"
            ? new XElement(ns + "TimeTrigger", new XElement(ns + "Repetition", new XElement(ns + "Interval", "PT" + (intervalSeconds / 60) + "M"), new XElement(ns + "StopAtDurationEnd", false)),
                new XElement(ns + "StartBoundary", DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss")), new XElement(ns + "Enabled", triggerEnabled))
            : new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", triggerEnabled), new XElement(ns + "UserId", LocalStore.UserId));
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", trigger),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"), new XElement(ns + "UserId", LocalStore.UserId), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false), new XElement(ns + "StartWhenAvailable", true), new XElement(ns + "WakeToRun", false), new XElement(ns + "ExecutionTimeLimit", strategy == "scheduler" ? "PT2M" : "PT0S"),
                strategy == "agent" ? new XElement(ns + "RestartOnFailure", new XElement(ns + "Interval", "PT1M"), new XElement(ns + "Count", 3)) : null),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(ns + "Exec", new XElement(ns + "Command", executable),
                new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();
    }
    public static async Task SetAsync(bool enabled, string executable, CancellationToken token, string? testId = null, string strategy = "agent", int intervalSeconds = 30)
    {
        string? file = null;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (enabled)
            {
                if (!File.Exists(executable)) throw new FileNotFoundException("Agent executable missing");
                file = Path.Combine(Path.GetTempPath(), "nufe-task-" + Guid.NewGuid().ToString("N") + ".xml");
                await File.WriteAllTextAsync(file, Xml(executable, testId == null, strategy, intervalSeconds), System.Text.Encoding.Unicode, token);
                foreach (var arg in new[] { "/Create", "/TN", ScopedName(testId), "/XML", file, "/F" }) start.ArgumentList.Add(arg);
            }
            else foreach (var arg in new[] { "/Delete", "/TN", ScopedName(testId), "/F" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Task scheduler unavailable");
            var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0 && enabled) throw new IOException("Logon task registration failed");
            if (process.ExitCode != 0 && !enabled && await ExistsAsync(token, testId)) throw new IOException("Logon task removal failed");
        }
        finally { if (file != null && File.Exists(file)) File.Delete(file); }
    }
    public static async Task<bool> ExistsAsync(CancellationToken token, string? testId = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/Query", "/TN", ScopedName(testId) }) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException("Task scheduler unavailable");
        var a = p.StandardOutput.ReadToEndAsync(token); var b = p.StandardError.ReadToEndAsync(token); await p.WaitForExitAsync(token); await Task.WhenAll(a, b); return p.ExitCode == 0;
    }
}
