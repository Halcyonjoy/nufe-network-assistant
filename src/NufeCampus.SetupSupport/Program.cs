using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using NufeCampus.Windows;

// Installer-only lifecycle operations. Never authenticate, start an agent, or open user data.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 10;
            string? Option(string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            if (args.Length == 0 || args[0] is not ("prepare" or "installed" or "uninstall" or "restore")) return 11;
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Option("--target") ?? AppContext.BaseDirectory));
            string? root = Option("--test-root");
            if (root != null)
            {
                root = LocalStore.ValidateTestRoot(root);
                if (!target.Equals(Path.Combine(root, "app"), StringComparison.OrdinalIgnoreCase)) return 12;
            }
            string? testId = root == null ? null : Path.GetFileName(root)["NufeCampus-test-".Length..];
            string taskName = StartupTask.Name + (testId == null ? "" : "-test-" + testId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            string pending = Path.Combine(Path.GetTempPath(), "nufe-setup-task-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24] + ".xml");
            if (args[0] == "restore")
            {
                if (!File.Exists(pending)) return 0;
                await ImportTask(taskName, XDocument.Load(pending), deadline.Token); File.Delete(pending); return 0;
            }
            var existing = File.Exists(pending) ? XDocument.Load(pending) : await QueryTask(taskName, deadline.Token);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var exec = existing?.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
            string? command = exec?.Element(ns + "Command")?.Value;
            bool ownScheduled = command != null && SamePath(command, Path.Combine(target, "NufeCampus.Scheduled.exe"));
            if (args[0] is "prepare" or "uninstall")
            {
                // Suspend the periodic trigger before replacing/removing its files. Keep its exact
                // original XML for rollback, including a user-disabled task and custom settings.
                if (ownScheduled)
                {
                    if (!File.Exists(pending)) await File.WriteAllTextAsync(pending, existing!.ToString(), Encoding.Unicode, deadline.Token);
                    var disabled = await Scheduler(["/Change", "/TN", taskName, "/Disable"], deadline.Token);
                    if (disabled.Exit != 0) return 23;
                }
                int stopped = await StopOwnProcesses(target, root, deadline.Token);
                if (stopped != 0)
                {
                    if (ownScheduled) { await ImportTask(taskName, existing!, deadline.Token); File.Delete(pending); }
                    return stopped;
                }
            }
            if (args[0] == "prepare") return 0;
            if (existing == null) return 0; // Fresh install never opts the user into startup.
            if (command == null || exec == null) return 20;
            if (args[0] == "uninstall")
            {
                // An old uninstaller must not remove a task migrated to a newer copy elsewhere.
                if (SamePath(command, Path.Combine(target, "NufeCampus.Agent.exe")) || ownScheduled)
                    await StartupTask.SetAsync(false, command, deadline.Token, testId);
                File.Delete(pending);
                return 0;
            }
            string filename = Path.GetFileName(command.Trim('"'));
            if (filename is not ("NufeCampus.Agent.exe" or "NufeCampus.Scheduled.exe")) return 20;
            string agent = Path.Combine(target, filename);
            if (!File.Exists(agent) || !File.Exists(Path.Combine(target, "NufeCampus.App.exe"))) return 21;
            exec.SetElementValue(ns + "Command", agent);
            exec.SetElementValue(ns + "WorkingDirectory", target);
            // Preserve triggers, settings, enabled state, and arguments. Only relocate the program.
            await ImportTask(taskName, existing, deadline.Token); File.Delete(pending); return 0;
        }
        catch { return 30; }
    }

    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first.Trim('"')), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static async Task ImportTask(string name, XDocument document, CancellationToken token)
    {
        string xml = Path.Combine(Path.GetTempPath(), "nufe-task-import-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(xml, document.ToString(), Encoding.Unicode, token);
            if ((await Scheduler(["/Create", "/TN", name, "/XML", xml, "/F"], token)).Exit != 0) throw new IOException("Task migration failed");
        }
        finally { File.Delete(xml); }
    }
    private static async Task<int> StopOwnProcesses(string target, string? testRoot, CancellationToken token)
    {
        // Refuse to discard unsaved UI edits; the installer tells the user to close the window.
        foreach (var process in Process.GetProcessesByName("NufeCampus.App"))
        {
            using (process) if (process.MainModule?.FileName is { } file && SamePath(file, Path.Combine(target, "NufeCampus.App.exe"))) return 40;
        }
        foreach (var process in Process.GetProcessesByName("NufeCampus.Agent"))
        {
            using (process)
            {
                if (process.MainModule?.FileName is not { } file || !SamePath(file, Path.Combine(target, "NufeCampus.Agent.exe"))) continue;
                var reply = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(testRoot), "status", token);
                if (reply.Status.ProcessId != process.Id) return 41;
                await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(testRoot), "stop", token);
                await process.WaitForExitAsync(token); // stop never logs out the campus session.
            }
        }
        foreach (var process in Process.GetProcessesByName("NufeCampus.SelfWorker"))
        {
            using (process) if (process.MainModule?.FileName is { } file && SamePath(file, Path.Combine(target, "NufeCampus.SelfWorker.exe"))) return 42;
        }
        foreach (string name in new[] { "NufeCampus.Cli", "NufeCampus.Scheduled" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) if (process.MainModule?.FileName is { } file && SamePath(file, Path.Combine(target, name + ".exe")))
                    await process.WaitForExitAsync(token);
        return 0;
    }

    private static async Task<XDocument?> QueryTask(string name, CancellationToken token)
    {
        var result = await Scheduler(["/Query", "/TN", name, "/XML"], token);
        if (result.Exit != 0)
        {
            // Distinguish a missing task from access/Task Scheduler failures.
            if (await StartupTask.ExistsAsync(token, name.Contains("-test-", StringComparison.Ordinal) ? name[(name.LastIndexOf("-test-", StringComparison.Ordinal) + 6)..] : null))
                throw new IOException("Task cannot be read");
            return null;
        }
        return XDocument.Parse(result.Output);
    }
    private static async Task<(int Exit, string Output)> Scheduler(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Task Scheduler unavailable");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); await Task.WhenAll(output, error);
        return (process.ExitCode, await output);
    }
}
