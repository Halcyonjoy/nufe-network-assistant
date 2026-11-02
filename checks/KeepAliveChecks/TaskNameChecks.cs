using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using NufeCampus.Windows;

internal static class TaskNameChecks
{
    public static async Task<int> RunAsync(string setupSupport)
    {
        int passed = 0, failed = 0;
        void Check(string label, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
        string root = LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
        string testId = Path.GetFileName(root)[16..];
        var store = new LocalStore(root);
        string app = Path.Combine(root, "app"); Directory.CreateDirectory(app);
        string host = Path.Combine(app, "NufeCampus.Scheduled.exe");
        File.WriteAllBytes(host, []); File.WriteAllBytes(Path.Combine(app, "NufeCampus.App.exe"), []);
        string[] names = ["", "南财校园网助手", "校园网定时认证", "已占用任务", "停用后的名称"];
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        async Task<int> Lifecycle(string operation)
        {
            string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { Path.GetFullPath(setupSupport), operation, "--target", app, "--test-root", root }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(limit.Token); return process.ExitCode;
        }
        try
        {
            var legacy = JsonSerializer.Deserialize("{\"Version\":1}", AppJson.Default.Settings)!;
            Check("Old settings resolve to the existing SID task name", legacy.TaskName == "" && StartupTask.ScopedName(taskName: legacy.TaskName) == StartupTask.Name);
            bool rejected = true;
            foreach (string bad in new[] { "a\\b", "a/b", "bad:name", "bad?name", "bad\nname", ".", "..", " name", new string('a', 129) })
            {
                try { store.UpdateSettings(s => s with { TaskName = bad }); rejected = false; } catch (InvalidDataException) { }
            }
            Check("Invalid names cannot overwrite stored preferences", rejected && store.ReadSettings().TaskName == "");
            using var lease = await RunnerLease.AcquireAsync(store, limit.Token);
            await StartupTask.RenameAsync(store, names[1], host, limit.Token, testId);
            Check("First-use naming does not register tasks, save credentials or enable keepalive/startup", !await StartupTask.ExistsAsync(limit.Token, testId, names[1]) && !store.HasSecret() && !store.ReadSettings().Enabled && !store.ReadSettings().AutoStart);
            store.UpdateSettings(s => s with { Enabled = false, AutoStart = false, KeepAliveStrategy = "scheduler", CheckIntervalSeconds = 120, ScheduledStopped = true, Memory = new(PausedByTool: true) });
            await StartupTask.SetAsync(true, host, limit.Token, testId, "scheduler", 120, names[1]); // Test triggers are disabled.
            var before = await StartupTask.QueryAsync(limit.Token, testId, names[1]);
            await StartupTask.RenameAsync(store, names[2], host, limit.Token, testId);
            var after = await StartupTask.QueryAsync(limit.Token, testId, names[2]);
            Check("Rename removes the old task and preserves trigger, interval, actions and settings", !await StartupTask.ExistsAsync(limit.Token, testId, names[1]) && after != null &&
                XNode.DeepEquals(before!.Root!.Element(ns + "Triggers"), after.Root!.Element(ns + "Triggers")) &&
                XNode.DeepEquals(before.Root.Element(ns + "Actions"), after.Root.Element(ns + "Actions")) &&
                XNode.DeepEquals(before.Root.Element(ns + "Settings"), after.Root.Element(ns + "Settings")));
            Check("Name survives reopen and installer read-only lookup without changing pause state", new LocalStore(root).ReadSettings().TaskName == names[2] && StartupTask.ReadConfiguredName(root) == names[2] && store.ReadSettings().Memory.PausedByTool && store.ReadSettings().ScheduledStopped);
            await StartupTask.SetAsync(true, host, limit.Token, testId, "scheduler", 120, names[3]);
            bool collision = false;
            try { await StartupTask.RenameAsync(store, names[3], host, limit.Token, testId); } catch (IOException) { collision = true; }
            Check("Occupied name leaves both original tasks and configured name intact", collision && store.ReadSettings().TaskName == names[2] && await StartupTask.ExistsAsync(limit.Token, testId, names[2]) && await StartupTask.ExistsAsync(limit.Token, testId, names[3]));
            string settingsFile = Path.Combine(root, "settings.json");
            bool rolledBack = false;
            File.SetAttributes(settingsFile, FileAttributes.ReadOnly);
            try { await StartupTask.RenameAsync(store, names[4], host, limit.Token, testId); }
            catch (UnauthorizedAccessException) { rolledBack = true; }
            finally { File.SetAttributes(settingsFile, FileAttributes.Normal); }
            Check("Settings write failure restores the old task and removes the staged task", rolledBack && store.ReadSettings().TaskName == names[2] && await StartupTask.ExistsAsync(limit.Token, testId, names[2]) && !await StartupTask.ExistsAsync(limit.Token, testId, names[4]));
            string task = StartupTask.ScopedName(testId, names[2]);
            var disable = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "/Change", "/TN", task, "/Disable" }) disable.ArgumentList.Add(arg);
            using (var process = Process.Start(disable)!) { await process.WaitForExitAsync(limit.Token); if (process.ExitCode != 0) throw new IOException("Test disable failed"); }
            await StartupTask.RenameAsync(store, names[4], host, limit.Token, testId);
            Check("User-disabled task remains disabled after rename", (await StartupTask.QueryAsync(limit.Token, testId, names[4]))?.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value == "false");
            Check("Installer prepare and restore find the custom task", await Lifecycle("prepare") == 0 && await Lifecycle("restore") == 0 && await StartupTask.ExistsAsync(limit.Token, testId, names[4]));
            Check("Upgrade preserves custom name and task interval", await Lifecycle("installed") == 0 && (await StartupTask.QueryAsync(limit.Token, testId, names[4]))?.Descendants(ns + "Interval").FirstOrDefault()?.Value == "PT2M");
            Check("Uninstall removes the owned custom task", await Lifecycle("uninstall") == 0 && !await StartupTask.ExistsAsync(limit.Token, testId, names[4]));
            store.SaveProfile(new("14000001", "synthetic-only"), "@cmcc", store.ReadSettings());
            string profile = store.ReadSettings().ActiveProfileId;
            store.SaveProfile(new("14000002", "synthetic-only"), "@cmcc", store.ReadSettings());
            store.UpdateSettings(s => s with { TaskName = names[1] });
            store.SelectProfile(profile);
            Check("Switching accounts retains the machine-local task name", store.ReadSettings().TaskName == names[1]);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            foreach (string name in names) await StartupTask.SetAsync(false, host, cleanup.Token, testId, taskName: name);
        }
        Console.WriteLine($"Passed {passed}, failed {failed}; isolated task naming only.");
        return failed == 0 ? 0 : 1;
    }
}
