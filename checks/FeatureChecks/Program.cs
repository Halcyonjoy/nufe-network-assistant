using NufeCampus.Windows;
using NufeCampus.SelfService;
using NufeCampus.Core;
using System.Net;
using System.Text;
using System.Text.Json;

int passed = 0, failed = 0; void Check(string name, bool result) { Console.WriteLine((result ? "PASS " : "FAIL ") + name); if (result) passed++; else failed++; }
string root = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")); var store = new LocalStore(root);
var a = new SecretPair("14000001", "alpha-secret"); var b = new SecretPair("14000002", "beta-secret");
if (args.Length == 2 && args[0] == "--verify-onboarding-saved")
{
    var isolated = new LocalStore(LocalStore.ValidateTestRoot(args[1]));
    Check("First-use UI encrypts the entered synthetic credential", isolated.ReadSecret() == new SecretPair("14000001", "onboarding-synthetic-2026"));
    Check("First-use UI registers the same account in the encrypted vault", isolated.Accounts() is [{ Account: "14000001" }]);
    var preferences = isolated.ReadSettings();
    Check("First encrypted save leaves keepalive and startup disabled", !preferences.Enabled && !preferences.AutoStart && !preferences.HttpConsent && !preferences.AutoOfflineEnabled);
    Check("First encrypted save does not manufacture a persistent user pause", !preferences.Memory.PausedByTool);
    var credentialPath = Path.Combine(isolated.Root, "campus.dpapi");
    var encoded = File.Exists(credentialPath) ? Encoding.UTF8.GetString(File.ReadAllBytes(credentialPath)) : "";
    Check("Saved credential file does not contain cleartext account or password", File.Exists(credentialPath) && !encoded.Contains("14000001") && !encoded.Contains("onboarding-synthetic-2026"));
    bool agentRunning = false;
    try { using var timeout = new CancellationTokenSource(1000); await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(isolated.Root), "status", timeout.Token); agentRunning = true; } catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
    Check("First encrypted save does not start the isolated agent", !agentRunning);
    store.RemoveOwnedData(); Console.WriteLine($"Passed {passed}, failed {failed}; actual isolated onboarding files only"); return failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--seed-ui")
{
    var seed = new LocalStore(LocalStore.ValidateTestRoot(args[1])); seed.SaveProfile(b, "@njxy", new() { Enabled = false, AutoStart = false, SelfConsent = true });
    seed.SaveProfile(a, "@cmcc", new() { Enabled = false, AutoStart = false, SelfConsent = true, ProtectedMacs = ["AABBCCDDEEFF"] });
    store.RemoveOwnedData(); Console.WriteLine("Isolated synthetic profiles ready; production state untouched."); return 0;
}
if (args.Length == 2 && args[0] == "--verify-enabled-ui")
{
    var isolated = new LocalStore(LocalStore.ValidateTestRoot(args[1])); var preferences = isolated.ReadSettings();
    Check("User explicitly enabled keepalive with consent without enabling startup", preferences.Enabled && preferences.HttpConsent && !preferences.AutoStart);
    Check("Enabling the first account is not blocked by an invented pause", !preferences.Memory.PausedByTool);
    using var timeout = new CancellationTokenSource(3000);
    var response = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(isolated.Root), "status", timeout.Token);
    Check("Published agent receives enabled preferences and credentials", response.Ok && response.Status.KeepAliveEnabled && response.Status.HasCredentials && !response.Status.Paused);
    Check("Isolated enabled agent remains off campus without authenticating", !response.Status.IsNufe && response.Status.Authentication == "unknown" && response.Status.LastRecovery == null);
    store.RemoveOwnedData(); Console.WriteLine($"Passed {passed}, failed {failed}; actual isolated UI and agent only"); return failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--verify-self-ui")
{
    var isolated = new LocalStore(LocalStore.ValidateTestRoot(args[1]));
    bool ok = isolated.ReadSelfSecret("14000001") == null && isolated.ReadSecret()?.Password == "ui-self-only";
    store.RemoveOwnedData(); Console.WriteLine(ok ? "PASS Actual UI saved the shared authentication credential" : "FAIL Actual UI self credential persistence"); return ok ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--verify-ui-save")
{
    var isolated = new LocalStore(LocalStore.ValidateTestRoot(args[1]));
    Check("Desktop save preserves the existing encrypted credential", isolated.ReadSecret() == a);
    var saved = isolated.ReadSettings();
    Check("Desktop save retains disabled nonconsenting test preferences", !saved.Enabled && !saved.AutoStart && !saved.HttpConsent);
    Check("Desktop save preserves both whitelist devices without enabling actions", saved.ProtectedMacs.Length == 2 && !saved.AutoOfflineEnabled);
    Check("Desktop save retains both account profiles", isolated.Accounts().Length == 2);
    store.RemoveOwnedData(); Console.WriteLine($"Passed {passed}, failed {failed}; actual isolated desktop files only"); return failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--stop-test-agent")
{
    string isolatedRoot = LocalStore.ValidateTestRoot(args[1]);
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var response = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(isolatedRoot), "stop", limit.Token);
    if (!response.Ok) throw new IOException("Isolated stop not confirmed");
    using var isolatedAgent = System.Diagnostics.Process.GetProcessById(response.Status.ProcessId); await isolatedAgent.WaitForExitAsync(limit.Token);
    store.RemoveOwnedData(); Console.WriteLine("Only isolated test agent stopped."); return 0;
}
try
{
    File.WriteAllText(Path.Combine(root, "settings.json"), """{"Version":1,"Enabled":true,"AutoStart":true,"HttpConsent":true,"Carrier":"@cmcc","Memory":{"PausedByTool":false,"CredentialsRejected":false,"AutoOfflineSuspended":false},"CredentialIssue":"","LastNotifiedIssue":""}""");
    var legacySettings = new LocalStore(root).ReadSettings();
    Check("Old settings load new fields safely without rewriting or enabling device actions", legacySettings.ProtectedMacs is { Length: 0 } && legacySettings.ActiveProfileId == "" && legacySettings.OfflineOrder == "oldest" && !legacySettings.AutoOfflineEnabled);
    var legacyStatus = JsonSerializer.Deserialize("""{"ProcessId":123,"Phase":"online","Issue":"","Internet":"reachable"}""", AppJson.Default.AgentStatus)!;
    Check("Old backend status preserves safe defaults for missing identity and message", legacyStatus.CurrentAccount == "" && legacyStatus.ServerMessage == "" && legacyStatus.Authentication == "unknown" && legacyStatus.ProtocolVersion == 0);
    var prefs = new Settings { AutoStart = false, Enabled = true, HttpConsent = true, SelfConsent = true, ProtectedMacs = ["AABBCCDDEEFF"], AutoOfflineEnabled = true };
    var selfA = new SecretPair(a.Account, "self-only-alpha"); var selfB = new SecretPair(b.Account, "self-only-beta");
    store.SaveLegacySelfSecretForMigration(selfA); store.SaveLegacySelfSecretForMigration(selfB); var selfReopen = new LocalStore(root);
    Check("Independent self-service credentials survive reopen and remain scoped to each account", selfReopen.ReadSelfSecret(a.Account) == selfA && selfReopen.ReadSelfSecret(b.Account) == selfB && selfReopen.ReadSelfSecret("14000003") == null);
    string selfCipher = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "self-service.dpapi")));
    Check("Self-service credential file contains no plaintext account or password", new[] { a.Account, b.Account, selfA.Password, selfB.Password }.All(v => !selfCipher.Contains(v)));
    store.SaveProfile(a, "@cmcc", prefs); var aId = store.Accounts().Single().Id;
    Check("Saving separate self credentials does not replace campus authentication credentials", store.ReadSecret() == a);
    Check("Verified legacy login migrates into the authentication credential", store.SharedCredential() == selfA && store.ReadSecret() == selfA && store.ReadSelfSecret(a.Account) == null);
    store.SelectProfile(aId); Check("Selecting profile retains the unified password", store.ReadSecret() == selfA);
    bool wrong = false; try { store.SaveSharedCredential(selfB); } catch (InvalidDataException) { wrong = true; }
    Check("Shared save cannot replace another selected account", wrong && store.ReadSecret() == selfA);
    store.SaveSharedCredential(a);

    store.SaveSecret(new("first-broadband", "first-broadband-secret"), true);
    store.SaveProfile(b, "@njxy", new() { AutoStart = false, Enabled = true, HttpConsent = true, SelfConsent = true, Memory = new(PausedByTool: true) });
    store.DeleteSecret(true);
    var reopened = new LocalStore(root); Check("Both accounts survive encrypted vault reopen", reopened.Accounts().Length == 2 && reopened.ReadSecret() == b);
    string cipher = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "accounts.dpapi")));
    Check("Vault contains no plaintext account, password or MAC", new[] { a.Account, b.Account, a.Password, b.Password, "AABBCCDDEEFF" }.All(v => !cipher.Contains(v)));
    reopened.SelectProfile(aId); Check("Selecting account restores its protected devices and credential", reopened.ReadSecret() == a && reopened.ReadSettings().ProtectedMacs.SequenceEqual(["AABBCCDDEEFF"]) && reopened.ReadSettings().AutoOfflineEnabled);
    Check("Broadband credential belongs to selected profile", reopened.ReadSecret(true)?.Account == "first-broadband");
    reopened.SelectProfile(reopened.Accounts().Single(p => p.Account == b.Account).Id); Check("New profile does not inherit another account's rules or broadband password", !reopened.ReadSettings().AutoOfflineEnabled && reopened.ReadSettings().ProtectedMacs.Length == 0 && !reopened.HasSecret(true));
    reopened.SelectProfile(aId); reopened.DeleteSecret(true); reopened.DeleteSecret(false); new LocalStore(root).SelectProfile(aId);
    Check("Recovering an account cannot resurrect an opted-out broadband password", !reopened.HasSecret(true));
    string vaultPath = Path.Combine(root, "accounts.dpapi"); var vault = File.ReadAllBytes(vaultPath); vault[vault.Length / 2] ^= 64; File.WriteAllBytes(vaultPath, vault);
    bool tampered = false; try { reopened.Accounts(); } catch (System.Security.Cryptography.CryptographicException) { tampered = true; }
    Check("Tampered vault is rejected", tampered); vault[vault.Length / 2] ^= 64; File.WriteAllBytes(vaultPath, vault);

    WifiState Own() => new("nufe", "192.0.2.1", "001122334455");
    OnlineDevice D(string mac, string time, string ip = "192.0.2.2") => new(mac, mac, ip, time, "1", "1", "fixture", "device");
    var rows = new[] { D("001122334455", "2026-10-01 00:00:00", "192.0.2.1"), D("AABBCCDDEEFF", "2026-10-02 00:00:00"), D("102030405060", "2026-10-03 00:00:00"), D("203040506070", "2026-10-04 00:00:00") };
    Check("Oldest policy protects own and listed devices", QuotaRecovery.Select(rows, prefs, Own())?.Mac == "102030405060");
    Check("Newest policy is configurable", QuotaRecovery.Select(rows, prefs with { OfflineOrder = "newest" }, Own())?.Mac == "203040506070");
    Check("Empty list disables automatic kick", QuotaRecovery.Select(rows, prefs with { ProtectedMacs = [] }, Own()) == null);
    Check("Manual strategy never chooses a target", QuotaRecovery.Select(rows, prefs with { OfflineOrder = "manual" }, Own()) == null);
    Check("Suspended rules remain stopped", QuotaRecovery.Select(rows, prefs with { Memory = new(AutoOfflineSuspended: true) }, Own()) == null);
    Check("Malformed MAC is rejected instead of loosely normalized", QuotaRecovery.StrictMac("xx001122334455") == null && QuotaRecovery.Select(rows, prefs with { ProtectedMacs = ["broken"] }, Own()) == null);
    Check("Uncertain own identity blocks automatic selection", QuotaRecovery.Select(rows, prefs, Own() with { Mac = null }) == null);
    Check("Own IP cannot be selected even when MAC changed", QuotaRecovery.Select([D("102030405060", "2026-10-03 00:00:00", "192.0.2.1")], prefs, Own()) == null);
    Check("Unknown login time is not used to choose oldest", QuotaRecovery.Select([D("102030405060", "unrecognized")], prefs, Own()) == null);

    store.SaveSecret(a); store.UpdateSettings(_ => new() { HttpConsent = true, AutoStart = false, Memory = new(PausedByTool: true) });
    var portal = new Portal(); var engine = new KeepAliveEngine(store, portal, Own, _ => { });
    var once = await engine.CommandAsync("authenticate_once", default); Check("Manual once authenticates but preserves persisted pause", once.Ok && store.ReadSettings().Memory.PausedByTool && portal.Logins == 1);
    var resume = await engine.CommandAsync("authenticate_resume", default); Check("Resume while already online refreshes without a duplicate login", resume.Ok && !store.ReadSettings().Memory.PausedByTool && portal.Logins == 1);
    store.UpdateSettings(_ => new() { HttpConsent = true, Memory = new(PausedByTool: true) }); portal = new() { Result = "campus_credentials" }; engine = new(store, portal, Own, _ => { });
    var rejection = await engine.CommandAsync("authenticate_resume", default); Check("Failed manual authentication does not clear pause", !rejection.Ok && store.ReadSettings().Memory.PausedByTool && store.ReadSettings().Memory.CredentialsRejected);
    store.UpdateSettings(_ => new() { HttpConsent = true, Memory = new(PausedByTool: true) }); portal = new() { ThrowOnLogin = true }; engine = new(store, portal, Own, _ => { });
    var unavailable = await engine.CommandAsync("authenticate_resume", default); Check("Manual network failure returns an actionable result without killing IPC dispatch", !unavailable.Ok && unavailable.Code == "network" && store.ReadSettings().Memory.PausedByTool);
    store.UpdateSettings(_ => new() { HttpConsent = true }); portal = new() { Result = "business_rejected" }; engine = new(store, portal, Own, _ => { }); await engine.TickAsync(true, default);
    engine = new(new LocalStore(root), portal, Own, _ => { }); await engine.TickAsync(true, default); Check("Unknown explicit business refusal blocks repeated submits across restart", portal.Logins == 1 && store.ReadSettings().CredentialIssue == "business_rejected");
    store.UpdateSettings(_ => new() { HttpConsent = true }); portal = new() { Online = true, OtherAccount = true }; engine = new(store, portal, Own, _ => { }); var mismatch = await engine.CommandAsync("authenticate_resume", default); Check("Different online account is displayed without switching", !mismatch.Ok && portal.Logins == 0 && portal.Logouts == 0 && engine.Status.CurrentAccount == "other@cmcc");
    using (var doc = JsonDocument.Parse("{\"msg\":\"Rad:Limit Users Err\"}")) Check("Carrier session limit is distinct from campus quota", CampusPortal.Classify(doc.RootElement) == "carrier_quota");
    using (var doc = JsonDocument.Parse("{\"msg\":\"unknown: alpha-secret 14000001 token=private-token 192.0.2.1\"}"))
    {
        string safe = CampusPortal.SafeMessage(doc.RootElement, a); Check("Server detail removes supplied password, account, token and IP", !new[] { a.Password, a.Account, "private-token", "192.0.2.1" }.Any(safe.Contains));
    }
    using (var doc = JsonDocument.Parse("{\"msg\":\"a+b%25 returned\"}")) Check("Server detail strips URL-decoded password variants", !CampusPortal.SafeMessage(doc.RootElement, new("14000001", "a+b%25")).Contains("a b%"));
    store.UpdateSettings(_ => prefs); portal = new() { Result = "quota" }; int recoveries = 0;
    engine = new(store, portal, Own, _ => { }, quotaRecovery: _ => { recoveries++; portal.Result = "ok"; return Task.FromResult("freed"); }); await engine.TickAsync(true, default);
    Check("Campus quota frees at most one slot then retries authentication once", recoveries == 1 && portal.Logins == 2 && engine.Status.Authentication == "online");
    portal.Online = false; portal.Result = "quota"; var afterKick = store.ReadSettings().LastAutomaticOffline;
    engine = new(store, portal, Own, _ => { }, () => afterKick!.Value.AddSeconds(10), quotaRecovery: _ => { recoveries++; return Task.FromResult("freed"); }); await engine.TickAsync(true, default);
    Check("Quota recurring within a minute suspends kicks rather than entering a device battle", recoveries == 1 && store.ReadSettings().Memory.AutoOfflineSuspended);
    store.UpdateSettings(_ => prefs); portal = new() { Result = "quota" }; recoveries = 0;
    engine = new(store, portal, Own, _ => { }, quotaRecovery: _ => { recoveries++; return Task.FromResult("freed"); }); await engine.TickAsync(true, default);
    engine = new(new LocalStore(root), portal, Own, _ => { }, quotaRecovery: _ => { recoveries++; return Task.FromResult("freed"); }); await engine.TickAsync(true, default);
    Check("Still over quota suspends further automatic kicks across restart", recoveries == 1 && portal.Logins == 2 && store.ReadSettings().Memory.AutoOfflineSuspended);
    store.UpdateSettings(_ => prefs with { OfflineOrder = "manual" }); portal = new() { Result = "quota" }; recoveries = 0;
    engine = new(store, portal, Own, _ => { }, quotaRecovery: _ => { recoveries++; return Task.FromResult("freed"); }); await engine.TickAsync(true, default);
    Check("Manual selection strategy only reports quota without running worker", recoveries == 0 && !store.ReadSettings().Memory.AutoOfflineSuspended);
    store.UpdateSettings(_ => prefs); portal = new() { Result = "carrier_quota" }; recoveries = 0;
    engine = new(store, portal, Own, _ => { }, quotaRecovery: _ => { recoveries++; return Task.FromResult("freed"); }); await engine.TickAsync(true, default); Check("Carrier quota never kicks campus devices automatically", recoveries == 0);
    store.UpdateSettings(_ => prefs with { Memory = new(PausedByTool: true) }); portal = new() { Online = true }; engine = new(store, portal, Own, _ => { }); await engine.TickAsync(false, default);
    Check("Paused keepalive still reports independent online identity without authentication", engine.Status.Authentication == "online" && engine.Status.CurrentAccount == "14000001@cmcc" && portal.Logins == 0 && engine.Status.Paused);
    store.UpdateSettings(s => s with { ActiveProfileId = "transition", Memory = new() }); portal = new(); engine = new(store, portal, Own, _ => { }); await engine.TickAsync(true, default);
    Check("Incomplete account switch fails closed instead of mixing rules", portal.Logins == 0 && engine.Status.Issue == "profile_transition");

    {
        string localRoot = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N")); var white = new LocalStore(localRoot);
        white.SaveProfile(a, "@cmcc", new() { AutoStart = false, AutoOfflineEnabled = false, Memory = new(PausedByTool: true) }); string id = white.Accounts().Single().Id;
        Check("Adding a device persists a whitelist without enabling automatic actions", white.AddProtectedMac(a.Account, "001122334455") && !new LocalStore(localRoot).ReadSettings().AutoOfflineEnabled && white.ReadSettings().Memory.PausedByTool);
        Check("Duplicate device does not create duplicate entries", !white.AddProtectedMac(a.Account, "001122334455") && white.ReadSettings().ProtectedMacs.Length == 1);
        bool wrongWhitelist = false; try { white.AddProtectedMac(b.Account, "AABBCCDDEEFF"); } catch (InvalidOperationException) { wrongWhitelist = true; }
        Check("A different session account cannot change this whitelist", wrongWhitelist && white.ReadSettings().ProtectedMacs.Length == 1);
        bool invalid = false; try { white.AddProtectedMac(a.Account, "not-a-mac"); } catch (ArgumentException) { invalid = true; }
        Check("Invalid MAC is rejected before local write", invalid);
        white.SaveProfile(b, "@njxy", new() { AutoStart = false }); white.SelectProfile(id); Check("Whitelist survives account switch and reopen", white.ReadSettings().ProtectedMacs.SequenceEqual(["001122334455"])); white.RemoveOwnedData();
    }
    {
        var wire = new SelfWire(0) { FullForm = true }; using var client = new SelfClient(wire, Own) { Consent = true }; await client.PrepareLoginAsync(default); await client.LoginAsync(a, "", default);
        SecretPair[] formEntries = [new("13800000001", "provided-a"), new("13800000002", "provided-b"), new("13800000003", "provided-c")];
        var ticket = await client.PrepareOperatorFormAsync(formEntries, default); await client.SubmitOperatorFormAsync(ticket, formEntries, default);
        Check("Three-row form sends each supplied account/password to its exact field", wire.Writes == 1 && wire.Safe && wire.Values.SequenceEqual(formEntries.SelectMany(r => new[] { r.Account, r.Password })));
        bool replay = false; try { await client.SubmitOperatorFormAsync(ticket, formEntries, default); } catch (SelfException e) { replay = e.Code == "expired"; }
        Check("Whole-form confirmation is single use", replay && wire.Writes == 1);
        var changed = await client.PrepareOperatorFormAsync(formEntries, default); formEntries[1] = new("13800000002", "changed-password"); bool edit = false; try { await client.SubmitOperatorFormAsync(changed, formEntries, default); } catch (SelfException e) { edit = e.Code == "changed"; }
        Check("Editing credentials after preview prevents submission", edit && wire.Writes == 1);
        bool partial = false; try { await client.PrepareOperatorFormAsync([new("13800000001", ""), new("", ""), new("", "")], default); } catch (SelfException e) { partial = e.Code == "broadband"; }
        Check("An account without a locally supplied password never submits", partial && wire.Writes == 1);
        var stale = await client.PrepareOperatorFormAsync(formEntries, default); wire.Values[0] = "server-changed"; bool race = false; try { await client.SubmitOperatorFormAsync(stale, formEntries, default); } catch (SelfException e) { race = e.Code == "changed"; }
        Check("Concurrent server binding change prevents whole-form overwrite", race && wire.Writes == 1);
        SecretPair[] clear = [new("", ""), new("", ""), new("", "")]; var empty = await client.PrepareOperatorFormAsync(clear, default); await client.SubmitOperatorFormAsync(empty, clear, default); Check("Explicit empty formEntries clear all groups only after their own confirmation", wire.Writes == 2 && wire.Values.All(x => x == ""));
    }
    for (int group = 0; group < 3; group++)
    {
        var wire = new SelfWire(group); using var client = new SelfClient(wire, Own) { Consent = true }; await client.PrepareLoginAsync(default); await client.LoginAsync(a, "", default);
        var ticket = await client.PrepareBindingAsync(false, "13800000000", "local-broadband", default, group); await client.BindAsync(ticket, "local-broadband", default);
        Check("Operator group " + group + " uses its own pair and never server password", wire.Writes == 1 && wire.Safe && wire.Values[group * 2] == "13800000000");
        var unbind = await client.PrepareBindingAsync(true, "13800000000", "local-broadband", default, group); await client.BindAsync(unbind, "local-broadband", default);
        Check("Operator group " + group + " unbind clears only guarded form", wire.Writes == 2 && wire.Values.All(v => v.Length == 0));
    }
    {
        var wire = new SelfWire(0) { OtherActive = true }; using var client = new SelfClient(wire, Own) { Consent = true }; await client.PrepareLoginAsync(default); await client.LoginAsync(a, "", default);
        bool blocked = false; try { await client.PrepareBindingAsync(false, "13800000000", "local-broadband", default, 0); } catch (SelfException e) { blocked = e.Code == "other_groups"; }
        Check("Other group password presence prevents a destructive full form", blocked && wire.Writes == 0);
    }
    {
        var wire = new SelfWire(0); using var client = new SelfClient(wire, Own) { Consent = true }; await client.PrepareLoginAsync(default); await client.LoginAsync(a, "", default);
        var bound = await client.BoundDevicesAsync(1, default); Check("Bound devices include offline records without counting them as online sessions", bound.Total == 1 && bound.Rows[0].Mac == "AABBCCDDEEFF");
        var ticket = await client.PrepareMacRemovalAsync(bound.Rows[0], default); await client.RemoveMacAsync(ticket, default); Check("MAC removal encodes exact MAC and verifies it disappeared", wire.Removals == 1 && (await client.BoundDevicesAsync(1, default)).Total == 0);
        bool blocked = false; try { await client.RemoveMacAsync(ticket, default); } catch (SelfException e) { blocked = e.Code == "expired"; }
        Check("MAC removal confirmation is single use", blocked && wire.Removals == 1);
        var features = await client.FeaturesAsync(default); Check("Platform navigation excludes write URLs and foreign origins", features.Length == 1 && features[0].Builtin && features[0].Name == "我的绑定设备");
        await client.LogoutAsync(default); bool signedOut = false; try { await client.OnlineAsync(default); } catch (SelfException e) { signedOut = e.Code == "session"; }
        Check("Self logout clears the session without another password submission", signedOut && !client.Authenticated && wire.LoginPosts == 1);
    }
    store.SaveProfile(a, "@cmcc", prefs);
    {
        var wire = new SelfWire(0); using var client = new SelfClient(wire, Own);
        string result = await QuotaRecovery.RunAsync(store, client, Own, default); Check("Automatic recovery performs one exact session offline through real adapter", result == "freed" && wire.Offlines == 1 && wire.LoginPosts == 1);
    }
    foreach (string mode in new[] { "empty", "manual", "blocked" })
    {
        store.UpdateSettings(_ => prefs with { ProtectedMacs = mode == "empty" ? [] : prefs.ProtectedMacs, OfflineOrder = mode == "manual" ? "manual" : "oldest", SelfLoginBlocked = mode == "blocked" });
        var wire = new SelfWire(0); using var client = new SelfClient(wire, Own); await QuotaRecovery.RunAsync(store, client, Own, default);
        Check("Unusable auto policy sends no login credential: " + mode, wire.LoginPosts == 0 && wire.Offlines == 0);
    }
    store.SaveProfile(a, "@cmcc", prefs);
    {
        var wire = new SelfWire(0) { BeforeOfflineForm = () => store.UpdateSettings(s => s with { AutoOfflineEnabled = false }) }; using var client = new SelfClient(wire, Own);
        bool blocked = false; try { await QuotaRecovery.RunAsync(store, client, Own, default); } catch (SelfException e) { blocked = e.Code == "changed"; }
        Check("Policy change during form fetch blocks the final offline request", blocked && wire.Offlines == 0);
    }
    using (var transport = new SpeedWire()) using (var speed = new SpeedMeasurement(false, transport))
    {
        var result = await speed.RunAsync(false, null, default); Check("Quick test payload remains under budget and excludes secrets", result.PayloadBytes <= 160_000_000 && result.PayloadBytes > 0 && transport.Safe && result.UploadMbps != null && result.DownloadMbps != null);
    }
    using (var transport = new SpeedWire(true)) using (var speed = new SpeedMeasurement(false, transport))
    {
        var result = await speed.RunAsync(false, null, default); Check("Short download body is incomplete rather than a fabricated completed speed", result.DownloadMbps == null && result.Limited);
    }
}
finally { store.RemoveOwnedData(); }
Console.WriteLine($"Passed {passed}, failed {failed}"); return failed == 0 ? 0 : 1;

sealed class Portal : ICampusPortal
{
    public bool Online, OtherAccount, ThrowOnLogin; public string Result = "ok"; public int Logins, Logouts;
    public Task<PortalState> ReadAsync(CancellationToken t) => Task.FromResult(new PortalState(Online, OtherAccount ? "other@cmcc" : "14000001@cmcc", "001122334455"));
    public bool Matches(PortalState state, string account, string carrier) => state.Online && !OtherAccount;
    public Task<LoginResult> AuthenticateAsync(SecretPair s, string c, CancellationToken t) { Logins++; if (ThrowOnLogin) throw new HttpRequestException("Synthetic outage"); Online = Result == "ok"; return Task.FromResult(new LoginResult(Online, Result)); }
    public Task LogoutAsync(SecretPair s, string c, CancellationToken t) { Logouts++; Online = false; return Task.CompletedTask; }
    public void Dispose() { }
}
sealed class SelfWire(int group) : HttpMessageHandler
{
    public string[] Values = new string[6].Select(_ => "").ToArray(); public bool OtherActive, FullForm, Safe = true; private bool logged, bound = true, online = true; public int Writes, Removals, Offlines, LoginPosts;
    public Action? BeforeOfflineForm;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath; string text = "";
        if (path == "/Self/login/") { logged = false; text = "<form action='/Self/login/verify' method='post'><input name='account'><input name='password'><input name='code'></form><div id='randomDiv' class='hide'></div>"; }
        else if (path == "/Self/login/randomCode") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        else if (path == "/Self/login/verify") { LoginPosts++; logged = true; return new(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri("/Self/dashboard", UriKind.Relative) } }; }
        else if (path == "/Self/login/logout") { logged = false; return new(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri("/Self/login/", UriKind.Relative) } }; }
        else if (!logged) return new(HttpStatusCode.Found) { Headers = { Location = new Uri("/Self/login/", UriKind.Relative) }, Content = new StringContent("") };
        else if (path == "/Self/dashboard/getOnlineList") text = online ? "[{\"sessionId\":\"one\",\"mac\":\"102030405060\",\"ip\":\"192.0.2.2\",\"loginTime\":\"2026-10-01 00:00:00\",\"useTime\":120,\"upFlow\":512,\"downFlow\":1024}]" : "[]";
        else if (path == "/Self/dashboard/tooffline") { Offlines++; online = false; text = "{}"; }
        else if (path == "/Self/dashboard") text = "<a href='/Self/service/myMac'>绑定设备</a><a href='/Self/service/unbindmac'>写操作</a><a href='/Self/dashboard/oprateMauthAction'>默认操作</a><a href='http://example.invalid/Self/service/myMac'>外部</a>";
        else if (path is "/Self/bill" or "/Self/service") text = "";
        else if (path == "/Self/service/getMacList") text = bound ? "{\"total\":1,\"rows\":[[0,\"AABBCCDDEEFF\",\"phone\",\"2026-10-01\",\"192.0.2.2\"]]}" : "{\"total\":0,\"rows\":[]}";
        else if (path == "/Self/service/unbindmac") { if (!request.RequestUri.Query.Contains("AABBCCDDEEFF")) throw new InvalidDataException(); Removals++; bound = false; text = "{}"; }
        else if (path == "/Self/service/operatorId")
        {
            BeforeOfflineForm?.Invoke();
            text = "<form method='post' action='/Self/service/bind-operator'><input name='csrftoken' value='csrf'>";
            for (int i = 0; i < 6; i++) text += $"<input name='FLDEXTRA{i + 1}' value='{(OtherActive && i == 5 ? "server-secret" : Values[i])}'>"; text += "</form>";
        }
        else if (path == "/Self/service/bind-operator")
        {
            Writes++; var fields = (await request.Content!.ReadAsStringAsync(token)).Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            Values = Enumerable.Range(1, 6).Select(n => fields["FLDEXTRA" + n]).ToArray();
            Safe &= !Values.Contains("server-secret") && (FullForm || Values.Where((_, i) => i != group * 2 && i != group * 2 + 1).All(v => v == "") && Values[group * 2 + 1] is "local-broadband" or ""); text = "{}";
        }
        else throw new InvalidDataException("Unexpected synthetic endpoint");
        return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
    }
}
sealed class SpeedWire(bool shortDownload = false) : HttpMessageHandler
{
    public bool Safe = true;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Safe &= request.RequestUri!.Host == "speed.cloudflare.com" && request.Headers.Authorization == null;
        if (request.Method == HttpMethod.Post) { await request.Content!.CopyToAsync(Stream.Null, token); return new(HttpStatusCode.OK) { Content = new StringContent("{}") }; }
        return new(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(request.RequestUri.Query.EndsWith("=0") ? 0 : shortDownload ? 4096 : 80_000_000)) };
    }
}
sealed class ZeroStream(long remaining) : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { int n = (int)Math.Min(count, remaining); Array.Clear(buffer, offset, n); remaining -= n; return n; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { token.ThrowIfCancellationRequested(); int n = (int)Math.Min(buffer.Length, remaining); buffer.Span[..n].Clear(); remaining -= n; return ValueTask.FromResult(n); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
