using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NufeCampus.Core;
using NufeCampus.Windows;

var checks = new List<(string Name, bool Passed)>();
Console.WriteLine("Runtime " + Environment.Version);
void Check(string name, bool success) { checks.Add((name, success)); Console.WriteLine($"{(success ? "PASS" : "FAIL")} {name}"); }
if (args.Contains("--updates-only")) return await UpdateChecks.RunAsync();
if (args.Contains("--release-check-only"))
{
    using var releaseHttp = ReleaseUpdates.CreateClient(); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var release = await new ReleaseUpdates(releaseHttp).CheckAsync(limit.Token);
    Console.WriteLine($"PASS public latest release lookup: {release.Tag}, {release.Size} bytes; installer not downloaded or executed");
    return 0;
}
if (args.Contains("--remove-data-only"))
{
    string dataRoot = LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
    var data = new LocalStore(dataRoot);
    data.SaveSecret(new("14000001", "synthetic-removal"));
    data.UpdateSettings(s => s with { Enabled = false });
    Directory.CreateDirectory(data.Logs);
    File.WriteAllText(Path.Combine(data.Logs, "events.log"), "synthetic");
    var run = new KeepAliveRunBuilder(KeepAliveSource.Scheduler); run.Observe("online"); data.RecordKeepAliveRun(run.Finish());
    string exported = Path.Combine(dataRoot, "manual-export.txt"); File.WriteAllText(exported, "synthetic export");
    data.RemoveOwnedData();
    Check("Remove credentials, settings, logs and history while preserving manual exports",
        Directory.GetFiles(dataRoot).Select(Path.GetFileName).SequenceEqual(new[] { "manual-export.txt" }) && !Directory.GetDirectories(dataRoot).Any());
    File.Delete(exported); Directory.Delete(dataRoot);
    return checks.All(c => c.Passed) ? 0 : 1;
}
if (args.Contains("--pause-only"))
{
    var pauseStore = new LocalStore(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")));
    try
    {
        pauseStore.SaveSecret(new("14000001", "synthetic-pause"));
        Check("Pause interrupts a stalled automatic HTTP check and persists promptly", await PauseIsPrompt(pauseStore));
        Console.WriteLine($"Passed {checks.Count(c => c.Passed)}, failed {checks.Count(c => !c.Passed)}; isolated stalled transport only");
        return checks.All(c => c.Passed) ? 0 : 1;
    }
    finally { pauseStore.RemoveOwnedData(); }
}
if (args.Length >= 3 && args[0] == "--measure")
{
    string measuredRoot = LocalStore.ValidateTestRoot(args[1]);
    using var deadline = new CancellationTokenSource(5000);
    var reply = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(measuredRoot), "status", deadline.Token);
    using var agent = Process.GetProcessById(reply.Status.ProcessId);
    using var ui = Process.GetProcessById(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
    if (agent.ProcessName != "NufeCampus.Agent" || ui.ProcessName != "NufeCampus.App") throw new InvalidOperationException("Unexpected measured processes");
    var startCpu = agent.TotalProcessorTime; var watch = Stopwatch.StartNew(); long maxAgent = 0, maxUi = 0, maxPrivate = 0;
    int workerOption = Array.IndexOf(args, "--worker"); Process? worker = null; long maxWorker = 0, maxCombined = 0; var uiStartCpu = ui.TotalProcessorTime;
    if (workerOption >= 0) { string workerRoot = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")); var launch = new ProcessStartInfo(Path.GetFullPath(args[workerOption + 1])) { UseShellExecute = false, CreateNoWindow = true }; launch.ArgumentList.Add("--test-root"); launch.ArgumentList.Add(workerRoot); worker = Process.Start(launch); }
    for (int i = 0; i < 40; i++) { agent.Refresh(); ui.Refresh(); long workerMemory = 0; if (worker != null && !worker.HasExited) { worker.Refresh(); workerMemory = worker.WorkingSet64; maxWorker = Math.Max(maxWorker, workerMemory); } maxCombined = Math.Max(maxCombined, agent.WorkingSet64 + ui.WorkingSet64 + workerMemory); maxAgent = Math.Max(maxAgent, agent.WorkingSet64); maxUi = Math.Max(maxUi, ui.WorkingSet64); maxPrivate = Math.Max(maxPrivate, agent.PrivateMemorySize64); await Task.Delay(500); }
    agent.Refresh(); watch.Stop(); double cpuMs = (agent.TotalProcessorTime - startCpu).TotalMilliseconds;
    int? workerExit = worker?.HasExited == true ? worker.ExitCode : null; worker?.Dispose(); ui.Refresh(); double uiCpuMs = (ui.TotalProcessorTime - uiStartCpu).TotalMilliseconds;
    string json = "{\"sampleMilliseconds\":" + watch.ElapsedMilliseconds + ",\"agentWorkingSetBytes\":" + maxAgent + ",\"agentPrivateBytes\":" + maxPrivate + ",\"uiWorkingSetBytes\":" + maxUi + ",\"workerWorkingSetBytes\":" + maxWorker + ",\"simultaneousCombinedPeakBytes\":" + maxCombined + ",\"workerExitCode\":" + (workerExit?.ToString() ?? "null") + ",\"uiCpuMilliseconds\":" + uiCpuMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"combinedWorkingSetBytes\":" + (maxAgent + maxUi + maxWorker) + ",\"agentCpuMilliseconds\":" + cpuMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"agentSingleCoreCpuPercent\":" + (cpuMs / watch.Elapsed.TotalMilliseconds * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"scope\":\"isolated off-campus agent, synthetic UI and optional synthetic NativeAOT worker; production agent excluded as a separate instance\"}";
    int output = Array.IndexOf(args, "--output"); if (output >= 0) await File.WriteAllTextAsync(args[output + 1], json);
    Console.WriteLine(json); return workerOption < 0 || workerExit == 0 ? 0 : 1;
}
if (args.Length >= 2 && args[0] == "--ui-state")
{
    var uiStore = new LocalStore(LocalStore.ValidateTestRoot(args[1]));
    Check("UI persists the correct synthetic account and password", uiStore.ReadSecret() == new SecretPair("14000001", "synthetic-ui-secret-2026"));
    Check("UI leaves broadband credential absent by default", !uiStore.HasSecret(true));
    Check("UI test preferences remain disabled and nonconsenting", !uiStore.ReadSettings().Enabled && !uiStore.ReadSettings().HttpConsent && !uiStore.ReadSettings().AutoStart);
    using var timeout = new CancellationTokenSource(5000);
    var response = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(uiStore.Root), "status", timeout.Token);
    Check("Closing UI leaves isolated published agent running", response.Ok && response.Status.HasCredentials);
    Console.WriteLine($"Passed {checks.Count(c => c.Passed)}, failed {checks.Count(c => !c.Passed)}");
    return checks.All(c => c.Passed) ? 0 : 1;
}
string root = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N"));
LocalStore.ValidateTestRoot(root); var store = new LocalStore(root);
var secret = new SecretPair("14000001", "synthetic secret-" + Guid.NewGuid().ToString("N"));
store.SaveSecret(secret); var reopened = new LocalStore(root);
Check("DPAPI account and password survive real file reopen", reopened.ReadSecret() == secret);
var bytes = File.ReadAllBytes(Path.Combine(root, "campus.dpapi"));
Check("Credential file contains neither plaintext account nor password", !Encoding.UTF8.GetString(bytes).Contains(secret.Account) && !Encoding.UTF8.GetString(bytes).Contains(secret.Password));
var rules = new DirectoryInfo(root).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
Check("Private data directory ACL excludes other principals", new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected && rules.All(r => r.IdentityReference.Value == LocalStore.UserId));
bytes[bytes.Length / 2] ^= 0x80; File.WriteAllBytes(Path.Combine(root, "campus.dpapi"), bytes);
bool rejected = false; try { reopened.ReadSecret(); } catch (CryptographicException) { rejected = true; }
Check("Tampered ciphertext rejected without plaintext fallback", rejected); store.SaveSecret(secret);
Check("Broadband credentials absent by default", !store.HasSecret(true));
store.SaveSecret(new("synthetic-broadband", "synthetic-broadband-password"), true);
Check("Broadband opt-in uses separate encrypted storage", store.ReadSecret() == secret && new LocalStore(root).ReadSecret(true)?.Account == "synthetic-broadband");
store.DeleteSecret(true); Check("Turning off broadband persistence deletes only broadband secret", !store.HasSecret(true) && store.ReadSecret() == secret);
store.UpdateSettings(s => s with { Enabled = true, HttpConsent = true, AutoStart = false });
store.UpdateSettings(s => s with { Memory = s.Memory with { PausedByTool = true } });
store.UpdateSettings(s => s with { Carrier = "@njxy" });
Check("Settings writes preserve concurrent policy fields", new LocalStore(root).ReadSettings().Memory.PausedByTool && store.ReadSettings().Carrier == "@njxy");
var preferences = new PreferencesBackup(1, true, false, "@cmcc");
string backup = JsonSerializer.Serialize(preferences, AppJson.Default.PreferencesBackup);
Check("Backup schema has no credential, session or consent fields", !backup.Contains("Password") && !backup.Contains(secret.Account) && !backup.Contains("HttpConsent"));

var log = new Diagnostics(store.Logs); log.Write(DiagnosticCode.Started);
string expired = Path.Combine(store.Logs, "expired.log"); File.WriteAllText(expired, "synthetic"); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-9));
for (int i = 0; i < 11; i++) File.WriteAllBytes(Path.Combine(store.Logs, "large-" + i + ".log"), new byte[1024 * 1024]);
log.Write(DiagnosticCode.Paused);
Check("Real log retention enforces seven days and ten MiB", !File.Exists(expired) && Directory.EnumerateFiles(store.Logs, "*.log").Sum(p => new FileInfo(p).Length) <= Diagnostics.Limit);

void Reset() => store.UpdateSettings(_ => new() { Enabled = true, AutoStart = false, HttpConsent = true });
var now = DateTimeOffset.UtcNow;
WifiState Nufe() => new("nufe", "192.0.2.1", "001122334455");
Reset(); var transport = new FakePortal(); var notices = new List<string>();
var engine = new KeepAliveEngine(store, transport, Nufe, notices.Add, () => now);
await engine.TickAsync(true, default); Check("Engine restores offline authentication once", transport.Logins == 1 && engine.Status.Phase == "online" && engine.Status.LastRecovery != null);
transport.Online = false; now = now.AddSeconds(30); await engine.TickAsync(true, default);
Check("External logout restores without a tool pause", transport.Logins == 2 && !store.ReadSettings().Memory.PausedByTool);
await engine.CommandAsync("pause", default); transport.Online = false;
var afterRestart = new KeepAliveEngine(new LocalStore(root), transport, Nufe, notices.Add, () => now);
await afterRestart.TickAsync(true, default); Check("Actual persisted pause prevents recovery after engine restart", transport.Logins == 2 && afterRestart.Status.Phase == "paused");
await afterRestart.CommandAsync("resume", default); now = now.AddSeconds(30); await afterRestart.TickAsync(true, default);
Check("Explicit resume permits recovery", transport.Logins == 3);
Reset(); var bad = new FakePortal { Result = "carrier_credentials" }; notices.Clear();
engine = new(store, bad, Nufe, notices.Add, () => now);
await engine.TickAsync(true, default);
for (int i = 0; i < 4; i++) { now = now.AddSeconds(30); await engine.TickAsync(true, default); }
engine = new(new LocalStore(root), bad, Nufe, notices.Add, () => now); await engine.TickAsync(true, default);
Check("Rejected credentials do not retry across events or restart", bad.Logins == 1 && store.ReadSettings().Memory.CredentialsRejected);
Check("One continuous credential fault notifies once across restart", notices.Count == 1);
await engine.CommandAsync("resume", default); await engine.TickAsync(true, default); Check("Resume alone does not clear rejected credentials", bad.Logins == 1);
bad.Result = "ok"; await engine.CommandAsync("retry", default); now = now.AddSeconds(30); await engine.TickAsync(false, default);
Check("Manual repair retry clears rejection and restores silently", bad.Logins == 2 && engine.Status.Phase == "online" && notices.Count == 1);
Reset(); var idle = new FakePortal(); engine = new(store, idle, () => new("off_campus"), notices.Add, () => now); await engine.TickAsync(true, default);
Check("Off-campus engine makes no portal calls", idle.Reads == 0 && idle.Logins == 0);
store.UpdateSettings(s => s with { HttpConsent = false }); engine = new(store, idle, Nufe, notices.Add, () => now); await engine.TickAsync(true, default);
Check("No HTTP consent means no credential or status transmission", idle.Reads == 0 && idle.Logins == 0);
Reset(); var simultaneous = new FakePortal { Slow = true }; engine = new(store, simultaneous, Nufe, notices.Add, () => now);
await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => engine.TickAsync(true, default)));
Check("Concurrent network events cannot submit duplicate authentication", simultaneous.Logins == 1);
Check("Pause interrupts a stalled automatic HTTP check and persists promptly", await PauseIsPrompt(store));
Reset(); var online = new FakePortal { Online = true }; notices.Clear();
engine = new(store, online, Nufe, notices.Add, () => now, _ => Task.FromResult(false));
await engine.TickAsync(true, default); now = now.AddSeconds(65); await engine.TickAsync(false, default); now = now.AddSeconds(30); await engine.TickAsync(false, default);
Check("External probe fault does not logout or authenticate", online.Logins == 0 && online.Logouts == 0 && engine.Status.Internet == "probe_failed");
Check("Persistent external probe fault notification deduplicated", notices.Count == 1);
Reset(); var broken = new FakePortal { FailLogout = true }; engine = new(store, broken, Nufe, notices.Add, () => now);
var down = await engine.CommandAsync("logout", default);
Check("Uncertain manual logout still leaves persistent pause", !down.Ok && store.ReadSettings().Memory.PausedByTool);

foreach (var (message, expected) in new[] { ("Rad:Passwd_Err", "carrier_credentials"), ("ldap auth error", "campus_credentials"), ("In use", "quota"), ("Rad:Status_Err", "carrier_state") })
{
    using var json = JsonDocument.Parse("{\"msg\":\"" + message + "\"}"); Check("Classify " + expected, CampusPortal.Classify(json.RootElement) == expected);
}
var wire = new WireHandler(); using (var portal = new CampusPortal(wire, Nufe))
{
    var logged = await portal.AuthenticateAsync(secret, "@cmcc", default);
    Check("Real protocol encoder and JSONP parser authenticate against fixture transport", logged.Success && wire.LoginCalls == 1 && wire.DestinationSafe);
}
wire = new(); using (var portal = new CampusPortal(wire, () => new("off_campus")))
{
    bool stopped = false; try { await portal.AuthenticateAsync(secret, "@cmcc", default); } catch (IOException) { stopped = true; }
    Check("Adapter off-campus guard blocks request before transport", stopped && wire.LoginCalls == 0);
}
wire = new() { Malicious = true }; using (var portal = new CampusPortal(wire, Nufe))
{
    bool stopped = false; try { await portal.ReadAsync(default); } catch (InvalidDataException) { stopped = true; } catch (JsonException) { stopped = true; }
    Check("Trailing JavaScript in portal data is rejected", stopped);
}

string pipeName = "NufeCampus-check-" + Guid.NewGuid().ToString("N"); using var serverStop = new CancellationTokenSource(); int invalid = 0;
var server = AgentPipe.ServeAsync(pipeName, _ => Task.FromResult(new AgentResponse(true, new() { Phase = "fixture" })), () => Interlocked.Increment(ref invalid), serverStop.Token);
using (var deadline = new CancellationTokenSource(4000)) { var reply = await AgentPipe.CallAsync(pipeName, "status", deadline.Token); Check("Real current-user named pipe roundtrip", reply.Ok && reply.Status.Phase == "fixture"); }
using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
{
    await client.ConnectAsync(3000); await client.WriteAsync(BitConverter.GetBytes(20000));
}
await Task.Delay(100);
using (var deadline = new CancellationTokenSource(4000)) { var reply = await AgentPipe.CallAsync(pipeName, "status", deadline.Token); Check("Oversized IPC frame rejected while server survives", invalid == 1 && reply.Ok); }
serverStop.Cancel(); try { await server; } catch (OperationCanceledException) { }

using (var concurrentStop = new CancellationTokenSource())
{
    string concurrentPipe = "NufeCampus-check-" + Guid.NewGuid().ToString("N");
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentServer = AgentPipe.ServeAsync(concurrentPipe, async (command, token) =>
    {
        if (command == "authenticate_once") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        return new AgentResponse(true, new() { Phase = command });
    }, () => { }, concurrentStop.Token);
    using var longLimit = new CancellationTokenSource(8000);
    var longCall = AgentPipe.CallAsync(concurrentPipe, "authenticate_once", longLimit.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    foreach (string command in new[] { "status", "stop" })
    {
        bool responsive = false;
        try { using var quick = new CancellationTokenSource(800); responsive = (await AgentPipe.CallAsync(concurrentPipe, command, quick.Token)).Ok; }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
        Check("IPC " + command + " remains responsive during a long request", responsive);
    }
    release.TrySetResult(); await longCall;
    concurrentStop.Cancel(); try { await concurrentServer; } catch (OperationCanceledException) { }
}

XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
string unresponsiveRoot = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N"));
using (var unresponsiveInstance = new Mutex(false, AgentLifetime.InstanceName(unresponsiveRoot)))
{
    bool refused = false;
    try { using var limit = new CancellationTokenSource(4000); await AgentLifetime.StopAsync(unresponsiveRoot, "C:\\missing\\NufeCampus.Agent.exe", limit.Token); }
    catch (IOException) { refused = true; }
    Check("Unresponsive running instance cannot be reported as stopped", refused && AgentLifetime.IsRunning(unresponsiveRoot));
}
using (var limit = new CancellationTokenSource(4000))
{
    await AgentLifetime.StopAsync(unresponsiveRoot, "C:\\missing\\NufeCampus.Agent.exe", limit.Token);
    Check("Stopping an already absent instance is idempotent", !AgentLifetime.IsRunning(unresponsiveRoot));
}
var taskXml = XDocument.Parse(StartupTask.Xml("C:\\NUFE test\\NufeCampus.Agent.exe"));
Check("Task has user logon, finite restart and no wake or periodic trigger", taskXml.Descendants(ns + "LogonTrigger").Count() == 1 && !taskXml.Descendants(ns + "Repetition").Any() && taskXml.Descendants(ns + "WakeToRun").Single().Value == "false" && taskXml.Descendants(ns + "Count").Single().Value == "3");

int packageIndex = Array.IndexOf(args, "--package");
if (packageIndex >= 0)
{
    string package = Path.GetFullPath(args[packageIndex + 1]); string executable = Path.Combine(package, "NufeCampus.Agent.exe");
    string processRoot = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")); var processStore = new LocalStore(processRoot); processStore.UpdateSettings(s => s with { Enabled = false, AutoStart = false });
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add("--test-root"); start.ArgumentList.Add(processRoot);
    using var agent = Process.Start(start) ?? throw new IOException("Test agent did not start");
    string processPipe = LocalStore.PipeName + LocalStore.TestSuffix(processRoot);
    AgentResponse? ready = null;
    for (int i = 0; i < 30; i++) { try { using var limit = new CancellationTokenSource(2000); ready = await AgentPipe.CallAsync(processPipe, "status", limit.Token); break; } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { await Task.Delay(100); if (agent.HasExited) break; } }
    Check("Published NativeAOT agent starts and serves IPC", ready?.Ok == true && ready.Status.ProcessId == agent.Id);
    if (ready != null)
    {
        using var limit = new CancellationTokenSource(6000); await AgentPipe.CallAsync(processPipe, "pause", limit.Token);
        Check("Published agent pause writes actual settings", processStore.ReadSettings().Memory.PausedByTool);
        using var duplicate = Process.Start(start) ?? throw new IOException("Duplicate launch failed");
        await duplicate.WaitForExitAsync(limit.Token);
        Check("A duplicate launch exits without replacing the active agent", duplicate.ExitCode == 0 && !agent.HasExited);
        await AgentLifetime.StopAsync(processRoot, executable, limit.Token);
        Check("Published agent exits normally on explicit stop", agent.ExitCode == 0);
        Check("Stopped agent releases its scoped instance marker", !AgentLifetime.IsRunning(processRoot));
    }
    if (!agent.HasExited) { agent.Kill(); await agent.WaitForExitAsync(); }
    string testId = Guid.NewGuid().ToString("N");
    try
    {
        using var limit = new CancellationTokenSource(10000); await StartupTask.SetAsync(true, executable, limit.Token, testId);
        Check("Disabled test logon task can be registered with current user", await StartupTask.ExistsAsync(limit.Token, testId));
    }
    catch (Exception e) when (e is IOException or OperationCanceledException) { Check("Disabled test logon task can be registered with current user", false); }
    finally { using var limit = new CancellationTokenSource(10000); await StartupTask.SetAsync(false, executable, limit.Token, testId); Check("Owned test task removed", !await StartupTask.ExistsAsync(limit.Token, testId)); }
}
if (args.Contains("--live-read"))
{
    var current = Wifi.Read();
    if (current.Kind == "nufe" && current.Address != null)
    {
        using var portal = new CampusPortal(); using var limit = new CancellationTokenSource(8000); var state = await portal.ReadAsync(limit.Token);
        if (state.Online) Check("Anonymous live status matches bound NUFE wireless MAC", Wifi.NormalizeMac(state.Mac) == Wifi.NormalizeMac(current.Mac ?? "") && state.Account.EndsWith("@cmcc", StringComparison.Ordinal) || state.Online && Wifi.NormalizeMac(state.Mac) == Wifi.NormalizeMac(current.Mac ?? "") && state.Account.EndsWith("@njxy", StringComparison.Ordinal));
        else Console.WriteLine("SKIP live online source identity: current source offline; valid status response received");
    }
    else Console.WriteLine("SKIP live status: NUFE-STU not positively identified");
}

string manual = Path.Combine(root, "manually-exported-backup.json"); File.WriteAllText(manual, "synthetic export");
store.RemoveOwnedData(); Check("Removal deletes owned data but preserves manual export", File.Exists(manual) && !File.Exists(Path.Combine(root, "campus.dpapi")) && !File.Exists(Path.Combine(root, "settings.json")));
Console.WriteLine($"Passed {checks.Count(c => c.Passed)}, failed {checks.Count(c => !c.Passed)}; isolated fixtures only");
return checks.All(c => c.Passed) ? 0 : 1;

static async Task<bool> PauseIsPrompt(LocalStore store)
{
    store.UpdateSettings(_ => new() { Enabled = true, AutoStart = false, HttpConsent = true });
    using var stalledWire = new StalledWireHandler();
    using var stalledPortal = new CampusPortal(stalledWire, () => new("nufe", "192.0.2.1", "001122334455"));
    using var tickStop = new CancellationTokenSource();
    var pausedEngine = new KeepAliveEngine(store, stalledPortal, () => new("nufe", "192.0.2.1", "001122334455"), _ => { });
    var tick = pausedEngine.TickAsync(true, tickStop.Token);
    await stalledWire.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    bool pausedPromptly = false;
    try { using var limit = new CancellationTokenSource(1200); pausedPromptly = (await pausedEngine.CommandAsync("pause", limit.Token)).Ok; }
    catch (OperationCanceledException) { }
    finally { tickStop.Cancel(); try { await tick; } catch (OperationCanceledException) { } }
    return pausedPromptly && store.ReadSettings().Memory.PausedByTool;
}

sealed class FakePortal : ICampusPortal
{
    public bool Online, Slow, FailLogout;
    public string Result = "ok";
    public int Logins, Reads, Logouts;
    public async Task<PortalState> ReadAsync(CancellationToken token) { Reads++; if (Slow) await Task.Delay(60, token); return new(Online, "14000001@cmcc", "001122334455"); }
    public Task<LoginResult> AuthenticateAsync(SecretPair secret, string carrier, CancellationToken token) { Logins++; Online = Result == "ok"; return Task.FromResult(new LoginResult(Online, Result)); }
    public Task LogoutAsync(SecretPair secret, string carrier, CancellationToken token) { Logouts++; if (FailLogout) throw new IOException("Synthetic uncertain logout"); Online = false; return Task.CompletedTask; }
    public bool Matches(PortalState state, string account, string carrier) => state.Online && state.Account == account + carrier;
    public void Dispose() { }
}
sealed class WireHandler : HttpMessageHandler
{
    public int LoginCalls;
    public bool DestinationSafe = true, Malicious;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        DestinationSafe &= request.RequestUri!.Host == "10.200.253.5" && request.RequestUri.Port == 80;
        if (request.RequestUri.AbsolutePath == "/drcom/login") LoginCalls++;
        string json = request.RequestUri.AbsolutePath == "/drcom/login" ? "{\"result\":1}" : "{\"result\":1,\"uid\":\"14000001@cmcc\",\"olmac\":\"001122334455\"}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("campusProbe(" + json + ");" + (Malicious ? "alert('x')" : ""), Encoding.UTF8, "application/javascript") });
    }
}
sealed class StalledWireHandler : HttpMessageHandler
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
        throw new InvalidOperationException("Canceled request should not complete");
    }
}
