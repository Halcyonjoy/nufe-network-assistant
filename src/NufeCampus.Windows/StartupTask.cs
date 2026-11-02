using System.Diagnostics;
using System.Xml.Linq;
using System.Text.Json;
namespace NufeCampus.Windows;

public static class StartupTask
{
    public static string Name => "NufeCampus-" + LocalStore.UserId;
    public static bool ValidName(string? name) => name != null && (name.Length == 0 ||
        name.Length <= 128 && name == name.Trim() && name is not ("." or "..") &&
        !name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)));
    public static string ScopedName(string? testId = null, string taskName = "")
    {
        if (!ValidName(taskName)) throw new ArgumentException("Invalid task name");
        string name = taskName.Length == 0 ? Name : taskName;
        if (testId == null) return name;
        if (!System.Text.RegularExpressions.Regex.IsMatch(testId, "^[0-9a-f]{32}$")) throw new ArgumentException("Invalid task test id");
        return name + "-test-" + testId;
    }
    // Installer lookup only: do not instantiate LocalStore or decrypt credentials here.
    public static string ReadConfiguredName(string? root = null)
    {
        string path = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NufeCampus"), "settings.json");
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Settings size rejected");
        var settings = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJson.Default.Settings) ?? throw new InvalidDataException("Settings invalid");
        if (!ValidName(settings.TaskName)) throw new InvalidDataException("Invalid task name");
        return settings.TaskName;
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
    public static async Task SetAsync(bool enabled, string executable, CancellationToken token, string? testId = null, string strategy = "agent", int intervalSeconds = 30, string taskName = "")
    {
        string name = ScopedName(testId, taskName);
        var existing = await QueryAsync(token, testId, taskName);
        if (existing != null && !Owns(existing, executable)) throw new IOException("Task name is occupied by another program");
        string? file = null;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (enabled)
            {
                if (!File.Exists(executable)) throw new FileNotFoundException("Agent executable missing");
                file = Path.Combine(Path.GetTempPath(), "nufe-task-" + Guid.NewGuid().ToString("N") + ".xml");
                await File.WriteAllTextAsync(file, Xml(executable, testId == null, strategy, intervalSeconds), System.Text.Encoding.Unicode, token);
                foreach (var arg in new[] { "/Create", "/TN", name, "/XML", file, "/F" }) start.ArgumentList.Add(arg);
            }
            else foreach (var arg in new[] { "/Delete", "/TN", name, "/F" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Task scheduler unavailable");
            var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0 && enabled) throw new IOException("Logon task registration failed");
            if (process.ExitCode != 0 && !enabled && await ExistsAsync(token, testId, taskName)) throw new IOException("Logon task removal failed");
        }
        finally { if (file != null && File.Exists(file)) File.Delete(file); }
    }
    public static async Task<bool> ExistsAsync(CancellationToken token, string? testId = null, string taskName = "")
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/Query", "/TN", ScopedName(testId, taskName) }) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException("Task scheduler unavailable");
        var a = p.StandardOutput.ReadToEndAsync(token); var b = p.StandardError.ReadToEndAsync(token); await p.WaitForExitAsync(token); await Task.WhenAll(a, b); return p.ExitCode == 0;
    }
    private static bool Owns(XDocument document, string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var actions = document.Root?.Element(ns + "Actions")?.Elements().ToArray();
        string? command = actions is [{ Name.LocalName: "Exec" } action] ? action.Element(ns + "Command")?.Value : null;
        string? user = document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "UserId")?.Value;
        if (command == null || user != LocalStore.UserId) return false;
        string path = Path.GetFullPath(command.Trim('"'));
        return string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(Path.GetFullPath(executable)), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path) is "NufeCampus.Agent.exe" or "NufeCampus.Scheduled.exe";
    }
    public static async Task<XDocument?> QueryAsync(CancellationToken token, string? testId = null, string taskName = "")
    {
        var result = await RunAsync(["/Query", "/TN", ScopedName(testId, taskName), "/XML"], token);
        if (result.Exit == 0) return XDocument.Parse(result.Output);
        if (await ExistsAsync(token, testId, taskName)) throw new IOException("Task cannot be read");
        return null;
    }
    // Caller holds RunnerLease. Never replace another task. Stage the new task disabled
    // so migration cannot temporarily introduce two active triggers.
    public static async Task RenameAsync(LocalStore store, string taskName, string executable, CancellationToken token, string? testId = null)
    {
        bool firstPreferences = !File.Exists(Path.Combine(store.Root, "settings.json"));
        string previous = store.ReadSettings().TaskName;
        string oldName = ScopedName(testId, previous), newName = ScopedName(testId, taskName);
        if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return;
        if (await QueryAsync(token, testId, taskName) != null) throw new IOException("Task name is occupied");
        var existing = await QueryAsync(token, testId, previous);
        if (existing == null)
        {
            store.UpdateSettings(s => s with { TaskName = taskName, Enabled = !firstPreferences && s.Enabled, AutoStart = !firstPreferences && s.AutoStart });
            return;
        }
        if (!Owns(existing, executable)) throw new IOException("Existing task belongs to another program");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var renamed = new XDocument(existing);
        renamed.Root?.Element(ns + "RegistrationInfo")?.SetElementValue(ns + "URI", "\\" + newName);
        var staged = new XDocument(renamed);
        var settings = staged.Root!.Element(ns + "Settings");
        if (settings == null) { settings = new XElement(ns + "Settings"); staged.Root.Add(settings); }
        settings.SetElementValue(ns + "Enabled", false);
        bool created = false, oldDeleted = false, saved = false;
        try
        {
            await ImportAsync(newName, staged, false, token); created = true;
            if ((await RunAsync(["/Delete", "/TN", oldName, "/F"], token)).Exit != 0) throw new IOException("Old task removal failed");
            oldDeleted = true;
            store.UpdateSettings(s => s with { TaskName = taskName }); saved = true;
            await ImportAsync(newName, renamed, true, token);
        }
        catch
        {
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (oldDeleted) await ImportAsync(oldName, existing, false, rollback.Token);
            if (saved) store.UpdateSettings(s => s with { TaskName = previous });
            if (created && (await RunAsync(["/Delete", "/TN", newName, "/F"], rollback.Token)).Exit != 0)
                throw new IOException("Task name rollback incomplete");
            throw;
        }
    }
    private static async Task ImportAsync(string name, XDocument document, bool replace, CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), "nufe-task-rename-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(path, document.ToString(), System.Text.Encoding.Unicode, token);
            string[] args = replace ? ["/Create", "/TN", name, "/XML", path, "/F"] : ["/Create", "/TN", name, "/XML", path];
            if ((await RunAsync(args, token)).Exit != 0) throw new IOException("Task name migration failed");
        }
        finally { File.Delete(path); }
    }
    private static async Task<(int Exit, string Output)> RunAsync(string[] args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Task scheduler unavailable");
        process.StandardInput.Close(); // Refuse overwrite confirmation when /F is absent.
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        // Observe a started mutation's result before deciding whether to roll it back.
        await process.WaitForExitAsync(); await Task.WhenAll(output, error);
        return (process.ExitCode, await output);
    }
}
