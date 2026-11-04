using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using NufeCampus.Core;
using NufeCampus.Windows;

if (args is ["--task-name-only", var setupSupport]) return await TaskNameChecks.RunAsync(setupSupport);
if (args is ["--history-only"]) return await HistoryChecks.RunAsync();
if (args is ["--history-process-only", var historyPackage]) return await HistoryChecks.ProcessesAsync(historyPackage);

int passed = 0, failed = 0;
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) passed++; else failed++; }
async Task<(int Exit, string Output)> Run(string executable, string[] arguments, string? root = null)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    start.Environment.Remove("NUFE_CAMPUS_TEST_ROOT"); start.Environment.Remove("NUFE_CAMPUS_SCHEDULED");
    if (root != null) start.Environment["NUFE_CAMPUS_TEST_ROOT"] = root;
    using var p = Process.Start(start) ?? throw new IOException("Cannot launch check");
    var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(85));
    await p.WaitForExitAsync(limit.Token); await Task.WhenAll(output, error);
    return (p.ExitCode, await output);
}
if (args is ["--observation-settings-only"])
{
    string preferenceRoot = LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
    var preferenceStore = new LocalStore(preferenceRoot);
    var oldSettings = JsonSerializer.Deserialize("{\"Version\":1}", AppJson.Default.Settings)!;
    var oldBackup = JsonSerializer.Deserialize("{\"Version\":1,\"Enabled\":false,\"AutoStart\":false,\"Carrier\":\"@cmcc\"}", AppJson.Default.PreferencesBackup)!;
    Check("Old settings and backups retain foreground defaults", oldSettings.ForegroundQuerySeconds == 3 && oldSettings.ForegroundProbeSeconds == 120 && oldBackup.ForegroundQuerySeconds == 3 && oldBackup.ForegroundProbeSeconds == 120);
    preferenceStore.UpdateSettings(s => s with { Enabled = false, AutoStart = false, ForegroundQuerySeconds = 20, ForegroundProbeSeconds = 5 });
    var reread = new LocalStore(preferenceRoot).ReadSettings();
    Check("Foreground preferences persist without saving credentials or enabling keepalive", reread.ForegroundQuerySeconds == 20 && reread.ForegroundProbeSeconds == 5 && !preferenceStore.HasSecret() && !reread.Enabled && !reread.AutoStart);
    var savedBackup = new PreferencesBackup(1, false, false, "@cmcc", ForegroundQuerySeconds: 20, ForegroundProbeSeconds: 5);
    var roundtrip = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(savedBackup, AppJson.Default.PreferencesBackup), AppJson.Default.PreferencesBackup)!;
    Check("Backup roundtrip preserves the two independent intervals", roundtrip.ForegroundQuerySeconds == 20 && roundtrip.ForegroundProbeSeconds == 5);
    byte[] priorSettings = File.ReadAllBytes(Path.Combine(preferenceRoot, "settings.json"));
    bool allRejected = true;
    foreach (var invalid in new[] { (0, 120), (3601, 120), (3, 4), (3, 86401) })
    {
        try { preferenceStore.UpdateSettings(s => s with { ForegroundQuerySeconds = invalid.Item1, ForegroundProbeSeconds = invalid.Item2 }); allRejected = false; }
        catch (InvalidDataException) { }
    }
    Check("Invalid interval saves are rejected without overwriting settings", allRejected && priorSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(preferenceRoot, "settings.json"))));
    preferenceStore.SaveProfile(new("14000001", "synthetic-only"), "@cmcc", preferenceStore.ReadSettings());
    string firstProfile = preferenceStore.ReadSettings().ActiveProfileId;
    preferenceStore.SaveProfile(new("14000002", "synthetic-only"), "@cmcc", preferenceStore.ReadSettings());
    preferenceStore.UpdateSettings(s => s with { ForegroundQuerySeconds = 30, ForegroundProbeSeconds = 10 });
    preferenceStore.SelectProfile(firstProfile, false);
    Check("Switching account retains global foreground preferences", preferenceStore.ReadSettings().ForegroundQuerySeconds == 30 && preferenceStore.ReadSettings().ForegroundProbeSeconds == 10);
    Console.WriteLine($"Passed {passed}, failed {failed}; observation preferences only."); return failed == 0 ? 0 : 1;
}
if (args is ["--foreground-only"])
{
    string observationRoot = LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
    var observationStore = new LocalStore(observationRoot);
    var responseHandler = new ObservationResponses();
    using var observationPortal = new CampusPortal(responseHandler, () => new("nufe", "192.0.2.10"));
    using var writer = RunnerLease.TryAcquire(observationStore) ?? throw new IOException("Test lease missing");
    foreach (var mode in new[] { "agent", "scheduler" })
    {
        observationStore.UpdateSettings(s => s with
        {
            KeepAliveStrategy = mode,
            CheckIntervalSeconds = 300,
            Enabled = false,
            AutoStart = false,
            ScheduledStopped = true,
            Memory = new(PausedByTool: true)
        });
        var settingsBefore = File.ReadAllBytes(Path.Combine(observationRoot, "settings.json"));
        responseHandler.Body = "campusProbe({\"result\":1,\"uid\":\"14000001@cmcc\"});";
        var online = await OneShotAuthentication.QueryAsync(observationStore, observationPortal, () => new("nufe"), CancellationToken.None);
        Check(mode + ": live online/account readable without credentials while keeper stopped and writer busy", online.Ok && online.Status.Authentication == "online" && online.Status.CurrentAccount == "14000001@cmcc" && !observationStore.HasSecret());
        responseHandler.Body = "campusProbe({\"result\":0});";
        var offline = await OneShotAuthentication.QueryAsync(observationStore, observationPortal, () => new("nufe"), CancellationToken.None);
        Check(mode + ": subsequent offline response replaces online state without changing persisted pause/settings", offline.Ok && offline.Status.Authentication == "offline" && offline.Status.CurrentAccount == "" && offline.Status.Paused && settingsBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(observationRoot, "settings.json"))));
    }
    responseHandler.Body = "unexpected response";
    var unknown = await OneShotAuthentication.QueryAsync(observationStore, observationPortal, () => new("nufe"), CancellationToken.None);
    Check("Unrecognized server response reports unknown rather than retaining online", !unknown.Ok && unknown.Status.Authentication == "unknown" && unknown.Status.CurrentAccount == "");
    Check("Observation uses only the read-only status endpoint and never starts Agent", responseHandler.ReadOnly && !AgentLifetime.IsRunning(observationRoot));
    Console.WriteLine($"Passed {passed}, failed {failed}; foreground read-only contract only."); return failed == 0 ? 0 : 1;
}
if (args is ["--seed-ui", var uiRoot])
{
    var uiStore = new LocalStore(LocalStore.ValidateTestRoot(uiRoot));
    uiStore.SaveProfile(new("14000001", "synthetic-cli-2026"), "@cmcc", new() { Enabled = false, AutoStart = false });
    Console.WriteLine("Synthetic UI root ready."); return 0;
}
if (args is ["--final-only", var finalPackage])
{
    string finalRoot = LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
    var finalStore = new LocalStore(finalRoot);
    string finalCli = Path.GetFullPath(Path.Combine(finalPackage, "NufeCampus.Cli.exe"));
    string finalAgent = Path.GetFullPath(Path.Combine(finalPackage, "NufeCampus.Agent.exe"));
    finalStore.SaveProfile(new("14000001", "synthetic-final-2026"), "@cmcc", new()
    {
        Enabled = false,
        AutoStart = false,
        KeepAliveStrategy = "scheduler",
        CheckIntervalSeconds = 60,
        ScheduledStopped = true
    });
    var manual = await Run(finalCli, ["--authenticate"], finalRoot);
    Check("Explicit CLI authentication remains one-shot even when scheduled keepalive is stopped", manual.Exit == 3 && JsonSerializer.Deserialize(manual.Output, AppJson.Default.AgentResponse)?.Code == "off_campus" && finalStore.ReadSettings().ScheduledStopped);
    finalStore.UpdateSettings(s => s with { KeepAliveStrategy = "agent", CheckIntervalSeconds = 10, Enabled = true, ScheduledStopped = false });
    var start = new ProcessStartInfo(finalAgent) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--test-root"); start.ArgumentList.Add(finalRoot);
    using var agentProcess = Process.Start(start) ?? throw new IOException("Agent launch failed");
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    string pipe = LocalStore.PipeName + LocalStore.TestSuffix(finalRoot);
    try
    {
        AgentStatus? first = null;
        for (int i = 0; i < 30 && first?.CheckedAt == null; i++)
        {
            try { var snapshot = (await AgentPipe.CallAsync(pipe, "status", limit.Token)).Status; if (snapshot.CheckedAt != default) first = snapshot; }
            catch (Exception error) when (error is IOException or TimeoutException) { }
            if (first == null) await Task.Delay(100);
        }
        AgentStatus? next = null;
        for (int i = 0; i < 28 && next == null; i++)
        {
            await Task.Delay(500, limit.Token);
            var snapshot = (await AgentPipe.CallAsync(pipe, "status", limit.Token)).Status;
            if (snapshot.CheckedAt > first?.CheckedAt) next = snapshot;
        }
        Check("Published Agent actually checks at the configured 10-second interval", first != null && next != null && next.CheckedAt - first.CheckedAt >= TimeSpan.FromSeconds(8) && next.CheckedAt - first.CheckedAt < TimeSpan.FromSeconds(15));
        var live = await Run(finalCli, ["--status"], finalRoot);
        Check("CLI queries through an existing Agent without creating a competing runner", live.Exit == 3 && !agentProcess.HasExited);
        await AgentPipe.CallAsync(pipe, "pause", limit.Token);
        var paused = await Run(finalCli, ["--authenticate"], finalRoot);
        Check("CLI respects the existing Agent's persisted pause", paused.Exit == 1 && JsonSerializer.Deserialize(paused.Output, AppJson.Default.AgentResponse)?.Code == "paused");
    }
    finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await AgentLifetime.StopAsync(finalRoot, finalAgent, stop.Token); }
    Check("Final isolated Agent exits with its instance marker released", agentProcess.HasExited && !AgentLifetime.IsRunning(finalRoot));
    finalStore.UpdateSettings(s => s with { KeepAliveStrategy = "scheduler", CheckIntervalSeconds = 60 });
    var suppressed = await Run(finalAgent, ["--test-root", finalRoot]);
    Check("Scheduler strategy refuses an accidental persistent Agent launch", suppressed.Exit == 0 && !AgentLifetime.IsRunning(finalRoot));
    Console.WriteLine($"Passed {passed}, failed {failed}; final changed paths only."); return failed == 0 ? 0 : 1;
}
if (args is not ["--package", var package]) throw new ArgumentException("--package directory required");
package = Path.GetFullPath(package);
string root = LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
var store = new LocalStore(root);
string testId = Path.GetFileName(root)[16..];
string app = Path.Combine(root, "app"); Directory.CreateDirectory(app);
string cli = Path.Combine(app, "NufeCampus.Cli.exe"), host = Path.Combine(app, "NufeCampus.Scheduled.exe");
string setup = Path.Combine(app, "NufeCampus.SetupSupport.exe"), agent = Path.Combine(app, "NufeCampus.Agent.exe");
foreach (string file in new[] { "NufeCampus.Cli.exe", "NufeCampus.Scheduled.exe", "NufeCampus.SetupSupport.exe", "NufeCampus.Agent.exe", "NufeCampus.App.exe" })
    File.Copy(Path.Combine(package, file), Path.Combine(app, file));
string scheduler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
string task = StartupTask.Name + "-test-" + testId;
XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
try
{
    File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Version\":1,\"Enabled\":false,\"AutoStart\":false}");
    var old = store.ReadSettings();
    Check("Old settings retain Agent strategy and 30-second default", old.KeepAliveStrategy == "agent" && old.CheckIntervalSeconds == 30);
    Check("Intervals reject sub-minute scheduled checks and allow configurable minutes", !KeepAliveConfiguration.IsValid("scheduler", 30) && !KeepAliveConfiguration.IsValid("scheduler", 90) && KeepAliveConfiguration.IsValid("scheduler", 300) && KeepAliveConfiguration.IsValid("agent", 45));
    bool rejected = false; try { store.UpdateSettings(s => s with { KeepAliveStrategy = "scheduler", CheckIntervalSeconds = 30 }); } catch (InvalidDataException) { rejected = true; }
    Check("Invalid saved interval is rejected without overwriting the existing configuration", rejected && store.ReadSettings() == old);
    store.SaveProfile(new("14000001", "synthetic-cli-2026"), "@cmcc", old);
    byte[] before = File.ReadAllBytes(Path.Combine(root, "settings.json"));
    var status = await Run(cli, ["--status"], root);
    var reply = JsonSerializer.Deserialize(status.Output, AppJson.Default.AgentResponse);
    Check("Published CLI queries live isolated state without enabling keepalive", status.Exit == 3 && reply is { Ok: true, Status.Phase: "off_campus" } && before.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "settings.json"))));
    var invalid = await Run(cli, ["--status", "--authenticate"], root);
    Check("CLI accepts exactly one of two supported arguments", invalid.Exit == 2 && (await Run(cli, ["--help"], root)).Exit == 2);
    using (var lease = RunnerLease.TryAcquire(store))
    {
        var busy = await Run(cli, ["--authenticate"], root);
        Check("Concurrent authentication/configuration is rejected rather than duplicated", lease != null && busy.Exit == 4);
    }
    // Exercise the actual credential guard before any network IO, including a configured
    // NUFE-STU identity. A handler throws if an unexpected request escapes the guard.
    using (var portal = new CampusPortal(new NoNetwork(), () => new("nufe", "192.0.2.1")))
    {
        store.UpdateSettings(s => s with { Memory = new(PausedByTool: true), HttpConsent = true });
        var engine = new KeepAliveEngine(store, portal, () => new("nufe", "192.0.2.1"), _ => { });
        var paused = await OneShotAuthentication.AuthenticateAsync(engine, store, lifetime.Token);
        Check("One-shot authentication preserves a manual pause without network access", !paused.Ok && paused.Code == "paused" && store.ReadSettings().Memory.PausedByTool);
        store.UpdateSettings(s => s with { Memory = new(CredentialsRejected: true) });
        var blocked = await OneShotAuthentication.AuthenticateAsync(engine, store, lifetime.Token);
        Check("Repeated one-shot invocations preserve credential rejection", !blocked.Ok && blocked.Code == "campus_credentials");
    }
    store.UpdateSettings(s => s with { Enabled = true, KeepAliveStrategy = "scheduler", CheckIntervalSeconds = 120, Memory = new(), HttpConsent = true });
    var xml = XDocument.Parse(StartupTask.Xml(host, false, "scheduler", 120));
    Check("Periodic task uses configured interval, interactive user and no wake-up", xml.Descendants(ns + "Interval").Single().Value == "PT2M" && xml.Descendants(ns + "WakeToRun").Single().Value == "false" && xml.Descendants(ns + "LogonType").Single().Value == "InteractiveToken" && xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value == "IgnoreNew");
    await StartupTask.SetAsync(true, host, lifetime.Token, testId, "scheduler", 120);
    var queried = await Run(scheduler, ["/Query", "/TN", task, "/XML"]);
    var actual = XDocument.Parse(queried.Output);
    Check("Windows accepts isolated periodic task with its trigger disabled", queried.Exit == 0 && actual.Descendants(ns + "TimeTrigger").Single().Element(ns + "Enabled")!.Value == "false" && actual.Descendants(ns + "Command").Single().Value == host);
    // The task action lives in validated TEMP/test-root/app, so the host infers test
    // scope even when Task Scheduler does not inherit this check process's environment.
    var requested = await Run(scheduler, ["/Run", "/TN", task]);
    for (int i = 0; i < 80 && store.ReadLastCheck() == null; i++) await Task.Delay(100);
    var checkedState = store.ReadLastCheck();
    Check("Actual scheduled action runs scoped CLI, records state and never starts Agent", requested.Exit == 0 && checkedState is { Phase: "off_campus", ProcessId: 0 } && !AgentLifetime.IsRunning(root));
    for (int i = 0; i < 50; i++)
    {
        using var lease = RunnerLease.TryAcquire(store); if (lease != null) break; await Task.Delay(100);
    }
    var prepared = await Run(setup, ["prepare", "--target", app, "--test-root", root]);
    var disabledTask = XDocument.Parse((await Run(scheduler, ["/Query", "/TN", task, "/XML"])).Output);
    Check("Upgrade preparation suspends the periodic task before replacing files", prepared.Exit == 0 && disabledTask.Root!.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value == "false");
    var installed = await Run(setup, ["installed", "--target", app, "--test-root", root]);
    var migrated = XDocument.Parse((await Run(scheduler, ["/Query", "/TN", task, "/XML"])).Output);
    Check("Upgrade restores task state and preserves periodic strategy and interval", installed.Exit == 0 && migrated.Descendants(ns + "Command").Single().Value == host && migrated.Descendants(ns + "Interval").Single().Value == "PT2M" && migrated.Root!.Element(ns + "Settings")!.Element(ns + "Enabled")?.Value != "false");
    store.UpdateSettings(s => s with { ScheduledStopped = true });
    var last = File.ReadAllBytes(Path.Combine(root, "last-check.json"));
    Check("Stopped schedule is not revived by a queued windowless invocation", (await Run(host, [], root)).Exit == 0 && last.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "last-check.json"))));
    var uninstalled = await Run(setup, ["uninstall", "--target", app, "--test-root", root]);
    Check("Uninstall removes the owned periodic task and retains encrypted user data", uninstalled.Exit == 0 && !await StartupTask.ExistsAsync(lifetime.Token, testId) && store.HasSecret());
    store.UpdateSettings(s => s with { KeepAliveStrategy = "agent", CheckIntervalSeconds = 45, ScheduledStopped = false });
    await StartupTask.SetAsync(true, agent, lifetime.Token, testId);
    var switched = XDocument.Parse((await Run(scheduler, ["/Query", "/TN", task, "/XML"])).Output);
    Check("Switching to Agent replaces the same task rather than leaving two strategies", switched.Descendants(ns + "LogonTrigger").Any() && !switched.Descendants(ns + "TimeTrigger").Any());
    await StartupTask.SetAsync(false, agent, lifetime.Token, testId);
}
catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); failed++; }
finally
{
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await StartupTask.SetAsync(false, host, cleanup.Token, testId);
    // Retain only synthetic evidence in TEMP; no production instance or real data is touched.
}
Console.WriteLine($"Passed {passed}, failed {failed}; isolated CLI/task lifecycle only.");
return failed == 0 ? 0 : 1;

sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Unexpected request escaped authentication guard");
}
sealed class ObservationResponses : HttpMessageHandler
{
    public string Body = "";
    public bool ReadOnly = true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        ReadOnly &= request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/drcom/chkstatus";
        if (!ReadOnly) throw new InvalidOperationException("Observation attempted a write endpoint");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body) });
    }
}
