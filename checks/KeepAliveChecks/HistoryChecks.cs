using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NufeCampus.Windows;

internal static class HistoryChecks
{
    private static readonly WifiState Campus = new("nufe", "192.0.2.1", "001122334455");
    private static LocalStore NewStore()
    {
        var store = new LocalStore(LocalStore.ValidateTestRoot(Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N"))));
        store.SaveProfile(new("14000001", "synthetic-history-password"), "@cmcc", new() { Enabled = true, AutoStart = false, HttpConsent = true });
        return store;
    }
    public static async Task<int> RunAsync()
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) passed++; else failed++; }
        var store = NewStore();
        var transport = new HistoryTransport { Online = true };
        using var portal = new CampusPortal(transport, () => Campus);
        var agent = new KeepAliveEngine(store, portal, () => Campus, _ => { }, historySource: KeepAliveSource.Agent);
        await agent.TickAsync(false, CancellationToken.None);
        var run = store.ReadKeepAliveHistory()[0];
        Check("Already-online execution is persisted without submitting authentication", run.Phase == "online" && !run.AuthenticationAttempted && run.Steps.Contains("authentication_online") && transport.Logins == 0);
        transport.Online = false;
        var scheduled = new KeepAliveEngine(store, portal, () => Campus, _ => { }, historySource: KeepAliveSource.Scheduler);
        await scheduled.TickAsync(false, CancellationToken.None);
        run = store.ReadKeepAliveHistory()[0];
        Check("Offline execution records authentication and confirmed success in order", run.Source == KeepAliveSource.Scheduler && run.AuthenticationAttempted && run.Phase == "online" &&
            Array.IndexOf(run.Steps, "authentication_offline") < Array.IndexOf(run.Steps, "authentication_requested") && run.Steps.Contains("authentication_confirmed") && transport.Logins == 1);
        transport.Online = false; transport.RejectLogin = true;
        var rejecting = new KeepAliveEngine(store, portal, () => Campus, _ => { }, historySource: KeepAliveSource.Agent);
        await rejecting.TickAsync(false, CancellationToken.None);
        run = store.ReadKeepAliveHistory()[0];
        Check("Rejected authentication records fixed reason without server text", run.Phase == "fault" && run.Issue == "campus_credentials" && run.AuthenticationAttempted);
        await rejecting.TickAsync(false, CancellationToken.None);
        run = store.ReadKeepAliveHistory()[0];
        Check("A protected subsequent check records the skip without repeating authentication", !run.AuthenticationAttempted && run.Phase == "fault" && transport.Logins == 2);
        store.UpdateSettings(s => s with { Memory = new(PausedByTool: true), CredentialIssue = "" });
        var paused = new KeepAliveEngine(store, portal, () => Campus, _ => { }, historySource: KeepAliveSource.Agent);
        await paused.TickAsync(false, CancellationToken.None);
        Check("Paused check is visible and submits no authentication", store.ReadKeepAliveHistory()[0].Phase == "paused" && transport.Logins == 2);
        var away = new KeepAliveEngine(store, portal, () => new("off_campus"), _ => { }, historySource: KeepAliveSource.Agent);
        await away.TickAsync(true, CancellationToken.None);
        Check("Network-triggered off-campus check records a skip", store.ReadKeepAliveHistory()[0] is { Phase: "off_campus", NetworkEvent: true, AuthenticationAttempted: false });
        int count = store.ReadKeepAliveHistory().Length;
        await OneShotAuthentication.QueryAsync(store, portal, () => Campus, CancellationToken.None);
        Check("Foreground read-only status queries do not generate keepalive history", store.ReadKeepAliveHistory().Length == count);
        string content = string.Join("", Directory.GetFiles(Path.Combine(store.Root, "keepalive-history")).Select(File.ReadAllText));
        Check("Persisted history excludes account, password, MAC, IP and raw response", new[] { "14000001", "synthetic-history-password", "001122334455", "192.0.2.1", "private-server-marker" }.All(value => !content.Contains(value, StringComparison.Ordinal)));
        Check("Records survive reopening the data store", new LocalStore(store.Root).ReadKeepAliveHistory().Length == count);
        string directory = Path.Combine(store.Root, "keepalive-history");
        string corrupt = Path.Combine(directory, "history-99999999-corrupt.jsonl");
        File.WriteAllText(corrupt, "not-json\n" + JsonSerializer.Serialize(run with { Issue = "private-server-marker" }, AppJson.Default.KeepAliveRun));
        Check("Malformed or untrusted record codes do not enter the visible history", store.ReadKeepAliveHistory().Length == count);
        File.Delete(corrupt);
        string expired = Path.Combine(directory, "history-20000101-expired.jsonl");
        File.WriteAllText(expired, JsonSerializer.Serialize(run, AppJson.Default.KeepAliveRun)); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
        store.RecordKeepAliveRun(run with { Id = Guid.NewGuid().ToString("N") });
        Check("Expired history is pruned on the next completed execution", !File.Exists(expired));
        string oversized1 = Path.Combine(directory, "history-20000102-size.jsonl"), oversized2 = Path.Combine(directory, "history-20000103-size.jsonl");
        File.WriteAllText(oversized1, new string('x', 3 * 1024 * 1024)); File.WriteAllText(oversized2, new string('x', 3 * 1024 * 1024));
        store.RecordKeepAliveRun(run with { Id = Guid.NewGuid().ToString("N") });
        Check("History storage remains within its size limit", Directory.GetFiles(directory).Sum(p => new FileInfo(p).Length) <= LocalStore.KeepAliveHistoryLimit);
        File.Delete(oversized1); File.Delete(oversized2);
        store.UpdateSettings(s => s with { Memory = new(), CredentialIssue = "", ScheduledStopped = false });
        transport.Online = true; transport.RejectLogin = false;
        byte[] before = File.ReadAllBytes(Path.Combine(store.Root, "settings.json"));
        var logout = await agent.CommandAsync("logout_once", CancellationToken.None);
        Check("Single logout confirms offline without changing keepalive or pause preferences", logout.Ok && logout.Status.Authentication == "offline" && transport.Logouts == 1 && before.SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root, "settings.json"))));
        logout = await agent.CommandAsync("logout_once", CancellationToken.None);
        Check("Already-offline single logout does not send another logout", logout.Ok && logout.Code == "already_offline" && transport.Logouts == 1);
        transport.Online = true; transport.Foreign = true;
        logout = await agent.CommandAsync("logout_once", CancellationToken.None);
        Check("Single logout protects a different authenticated account", !logout.Ok && logout.Code == "different_account" && transport.Logouts == 1);
        transport.Foreign = false; transport.ForeignMac = true;
        logout = await agent.CommandAsync("logout_once", CancellationToken.None);
        Check("Single logout also protects a different terminal using the same account", !logout.Ok && logout.Code == "different_account" && transport.Logouts == 1);
        transport.ForeignMac = false; transport.StickyLogout = true;
        logout = await agent.CommandAsync("logout_once", CancellationToken.None);
        Check("Unconfirmed logout is reported once without replay or pausing keepalive", !logout.Ok && logout.Code == "logout_unconfirmed" && transport.Logouts == 2 && !store.ReadSettings().Memory.PausedByTool);
        store.UpdateSettings(s => s with { Enabled = false, Memory = new(PausedByTool: true), ScheduledStopped = true });
        transport.StickyLogout = false;
        before = File.ReadAllBytes(Path.Combine(store.Root, "settings.json"));
        logout = await OneShotLogout.RunAsync(store, portal, () => Campus, CancellationToken.None);
        Check("Single logout also preserves an already stopped or paused keeper", logout.Ok && before.SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root, "settings.json"))));
        Console.WriteLine($"History UI root: {store.Root}");
        Console.WriteLine($"Passed {passed}, failed {failed}; isolated history and single logout only.");
        return failed == 0 ? 0 : 1;
    }
    public static async Task<int> ProcessesAsync(string package)
    {
        var store = NewStore(); string app = Path.Combine(store.Root, "app"); Directory.CreateDirectory(app);
        foreach (string path in Directory.GetFiles(Path.GetFullPath(package))) File.Copy(path, Path.Combine(app, Path.GetFileName(path)));
        store.UpdateSettings(s => s with { KeepAliveStrategy = "scheduler", CheckIntervalSeconds = 60 });
        async Task<int> Run()
        {
            var start = new ProcessStartInfo(Path.Combine(app, "NufeCampus.Scheduled.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["NUFE_CAMPUS_TEST_ROOT"] = store.Root;
            start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await process.WaitForExitAsync(timeout.Token); return process.ExitCode;
        }
        int exit = await Run(); var records = store.ReadKeepAliveHistory();
        bool first = exit == 3 && records.Length == 1 && records[0].Phase == "off_campus" && records[0].Source == KeepAliveSource.Scheduler && records[0].Steps.Contains("process_completed");
        Console.WriteLine((first ? "PASS " : "FAIL ") + "Scheduled host stores exactly one child execution report");
        using (var lease = RunnerLease.TryAcquire(store)!) { exit = await Run(); }
        bool second = exit == 4 && store.ReadKeepAliveHistory()[0].Phase == "busy";
        Console.WriteLine((second ? "PASS " : "FAIL ") + "Busy child exit is recorded even without an engine report");
        store.UpdateSettings(s => s with { ScheduledStopped = true }); exit = await Run();
        bool third = exit == 0 && store.ReadKeepAliveHistory()[0] is { Phase: "disabled", Issue: "stopped" };
        Console.WriteLine((third ? "PASS " : "FAIL ") + "Stopped invocation records why it skipped authentication");
        return first && second && third ? 0 : 1;
    }
    private sealed class HistoryTransport : HttpMessageHandler
    {
        public bool Online, RejectLogin, Foreign, ForeignMac, StickyLogout;
        public int Logins, Logouts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/drcom/chkstatus": body = Online ? "{\"result\":1,\"uid\":\"" + (Foreign ? "14000002" : "14000001") + "@cmcc\",\"olmac\":\"" + (ForeignMac ? "AABBCCDDEEFF" : "001122334455") + "\"}" : "{\"result\":0}"; break;
                case "/drcom/login": Logins++; Online = !RejectLogin; body = Online ? "{\"result\":1}" : "{\"result\":0,\"msg\":\"ldap auth error private-server-marker synthetic-history-password\"}"; break;
                case "/drcom/logout": Logouts++; if (!StickyLogout) Online = false; body = "{\"result\":1}"; break;
                default: throw new InvalidOperationException("Unexpected synthetic campus endpoint");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("campusProbe(" + body + ");", Encoding.UTF8) });
        }
    }
}
