using NufeCampus.Core;
namespace NufeCampus.Windows;

public sealed class KeepAliveEngine
{
    private readonly LocalStore store;
    private readonly ICampusPortal portal;
    private readonly Func<WifiState> wifi;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string> notify;
    private readonly Diagnostics log;
    private readonly Func<CancellationToken, Task<bool>>? internetProbe;
    private readonly Func<CancellationToken, Task<string>>? quotaRecovery;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? automaticCheck;
    private int pauseWaiters;
    private KeepAlivePolicy policy;
    private AgentStatus status = new() { ProcessId = Environment.ProcessId, ProtocolVersion = 2 };
    private DateTimeOffset nextAttempt, faultSince;
    private DateTimeOffset nextInternetProbe;
    private string activeFault = "";
    public AgentStatus Status => Volatile.Read(ref status);
    public KeepAliveEngine(LocalStore store, ICampusPortal portal, Func<WifiState> wifi, Action<string> notify, Func<DateTimeOffset>? clock = null, Func<CancellationToken, Task<bool>>? internetProbe = null, Func<CancellationToken, Task<string>>? quotaRecovery = null)
    {
        this.store = store; this.portal = portal; this.wifi = wifi; this.notify = notify; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.internetProbe = internetProbe; this.quotaRecovery = quotaRecovery;
        var initial = store.ReadSettings(); policy = new(initial.Memory); status = status with { KeepAliveEnabled = initial.Enabled }; log = new(store.Logs); log.Write(DiagnosticCode.Started);
    }
    private void Remember() => store.UpdateSettings(s => s with { Memory = policy.Memory });
    private void Publish(string phase, string issue = "", bool? nufe = null)
    {
        var old = Status;
        Volatile.Write(ref status, old with
        {
            Phase = phase,
            Issue = issue,
            IsNufe = nufe ?? old.IsNufe,
            Paused = policy.Memory.PausedByTool,
            HasCredentials = store.HasSecret(),
            CheckedAt = clock()
        });
        if (issue.Length == 0)
        {
            if (phase is "online" or "off_campus" or "paused" or "disabled") { activeFault = ""; faultSince = default; }
            return;
        }
        if (activeFault != issue) { activeFault = issue; faultSince = clock(); }
        bool persistent = issue is not ("network" or "unknown_response" or "external_probe") || clock() - faultSince >= TimeSpan.FromMinutes(1);
        var settings = store.ReadSettings();
        if (Status.IsNufe && persistent && settings.LastNotifiedIssue != issue)
        {
            store.UpdateSettings(s => s with { LastNotifiedIssue = issue }); notify(issue);
        }
    }
    public async Task TickAsync(bool networkEvent, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) return;
        var lifetimeToken = token;
        using var automatic = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = automatic.Token;
        Volatile.Write(ref automaticCheck, automatic);
        try
        {
            if (Volatile.Read(ref pauseWaiters) > 0) return;
            if (networkEvent) policy.NetworkRecoveryEvent();
            var settings = store.ReadSettings(); var network = wifi();
            Volatile.Write(ref status, Status with { KeepAliveEnabled = settings.Enabled });
            if (network.Kind != "nufe") { Volatile.Write(ref status, Status with { Authentication = "unknown", CurrentAccount = "", Internet = "unknown" }); Publish(network.Kind == "permission" ? "wifi_permission" : "off_campus", nufe: false); return; }
            if ((!settings.Enabled || policy.Memory.PausedByTool) && settings.HttpConsent)
            {
                try { using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(token); readLimit.CancelAfter(TimeSpan.FromSeconds(4)); var observed = await portal.ReadAsync(readLimit.Token); Volatile.Write(ref status, Status with { Authentication = observed.Online ? "online" : "offline", CurrentAccount = observed.Account }); }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException) { Volatile.Write(ref status, Status with { Authentication = "unknown", CurrentAccount = "" }); }
            }
            if (!settings.Enabled) { Publish("disabled", nufe: true); return; }
            if (policy.Memory.PausedByTool) { Publish("paused", nufe: true); return; }
            if (!settings.HttpConsent) { Publish("consent_required", nufe: true); return; }
            if (!store.HasSecret()) { Publish("no_credentials", nufe: true); return; }
            Volatile.Write(ref status, Status with { IsNufe = true });
            SecretPair? secret;
            try { secret = store.ReadSecret(); }
            catch (System.Security.Cryptography.CryptographicException) { Publish("fault", "credentials_unreadable"); return; }
            if (secret == null) { Publish("no_credentials"); return; }
            if (!store.MatchesActiveProfile(settings, secret)) { Publish("fault", "profile_transition"); return; }
            if (network.Address == null) { Publish("unreachable", "network"); return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(55));
            var current = await portal.ReadAsync(deadline.Token);
            Volatile.Write(ref status, Status with { CurrentAccount = current.Account, Authentication = current.Online ? "online" : "offline" });
            if (current.Online)
            {
                if (!portal.Matches(current, secret.Account, settings.Carrier)) { Publish("fault", "different_account"); return; }
                policy.ObserveOutcome(AuthenticationOutcome.ConfirmedOnline, clock());
                if (internetProbe != null && clock() >= nextInternetProbe)
                {
                    nextInternetProbe = clock().AddMinutes(2);
                    bool connected = await internetProbe(deadline.Token);
                    Volatile.Write(ref status, Status with { Internet = connected ? "reachable" : "probe_failed" });
                }
                if (!policy.Memory.CredentialsRejected) store.UpdateSettings(s => s with { CredentialIssue = "", LastNotifiedIssue = Status.Internet == "probe_failed" ? s.LastNotifiedIssue : "" });
                Publish("online", policy.Memory.CredentialsRejected ? settings.CredentialIssue : Status.Internet == "probe_failed" ? "external_probe" : ""); return;
            }
            if (settings.CredentialIssue is "quota" or "carrier_state" or "campus_missing" or "carrier_account" or "carrier_quota" or "business_rejected" or "auto_offline_suspended") { Publish("fault", settings.CredentialIssue); return; }
            var decision = policy.Evaluate(new(true, true, AuthenticationState.Offline, true), clock());
            if (decision.Work == Work.RequestCredentialRepair) { Publish("fault", settings.CredentialIssue.Length == 0 ? "campus_credentials" : settings.CredentialIssue); return; }
            if (decision.Work != Work.Authenticate || clock() < nextAttempt) { Publish("offline", activeFault); return; }
            nextAttempt = clock().AddSeconds(5); Publish("authenticating");
            var result = await AuthenticateTarget(secret, settings, deadline.Token);
            if (result.Success)
            {
                policy.ObserveOutcome(AuthenticationOutcome.ConfirmedOnline, clock());
                Volatile.Write(ref status, Status with { LastRecovery = clock(), Authentication = "online", CurrentAccount = secret.Account + settings.Carrier, ServerMessage = "", FailureAt = null });
                store.UpdateSettings(s => s with { LastNotifiedIssue = "", CredentialIssue = "" });
                log.Write(DiagnosticCode.AuthConfirmed); Publish("online");
            }
            else
            {
                Volatile.Write(ref status, Status with { ServerMessage = result.ServerMessage, FailureAt = clock() });
                if (result.Code is "campus_credentials" or "carrier_credentials")
                {
                    policy.ObserveOutcome(result.Code == "carrier_credentials" ? AuthenticationOutcome.CarrierCredentialsRejected : AuthenticationOutcome.CampusCredentialsRejected, clock());
                    Remember(); store.UpdateSettings(s => s with { CredentialIssue = result.Code }); log.Write(DiagnosticCode.AuthRejected);
                }
                else if (result.Code is "quota" or "carrier_state" or "campus_missing" or "carrier_account" or "carrier_quota" or "business_rejected" or "auto_offline_suspended")
                {
                    store.UpdateSettings(s => s with { CredentialIssue = result.Code }); log.Write(DiagnosticCode.QuotaRequired);
                }
                else { policy.ObserveOutcome(AuthenticationOutcome.TemporaryFailure, clock()); log.Write(DiagnosticCode.ProtocolUnknown); }
                Publish("fault", result.Code);
            }
        }
        catch (OperationCanceledException) when (!lifetimeToken.IsCancellationRequested && Volatile.Read(ref pauseWaiters) > 0) { }
        catch (OperationCanceledException) when (!lifetimeToken.IsCancellationRequested) { NetworkFault(); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            policy.ObserveOutcome(AuthenticationOutcome.TemporaryFailure, clock()); log.Write(DiagnosticCode.ProtocolUnknown); Publish("fault", "unknown_response");
        }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException) { NetworkFault(); }
        finally { Volatile.Write(ref automaticCheck, null); gate.Release(); }
    }
    private void SuspendAutoOffline() { policy.SuspendAutomaticDeviceOffline(); Remember(); }
    private async Task<LoginResult> AuthenticateTarget(SecretPair secret, Settings settings, CancellationToken token)
    {
        var result = await portal.AuthenticateAsync(secret, settings.Carrier, token);
        if (result.Success || result.Code != "quota" || !settings.AutoOfflineEnabled || settings.OfflineOrder == "manual" || quotaRecovery == null) return result;
        if (policy.Memory.AutoOfflineSuspended) return new(false, "auto_offline_suspended", QuotaMessage("stopped"));
        if (settings.LastAutomaticOffline is { } last && clock() - last < TimeSpan.FromMinutes(1)) { SuspendAutoOffline(); return new(false, "auto_offline_suspended", "设备处理：一分钟内再次超限，疑似设备重连冲突，已停用自动下线。"); }
        string recovery;
        try { recovery = await quotaRecovery(token); } catch (Exception error) when (error is OperationCanceledException or IOException or HttpRequestException) { SuspendAutoOffline(); return new(false, "auto_offline_suspended", QuotaMessage("uncertain")); }
        if (recovery != "freed") { SuspendAutoOffline(); return new(false, "auto_offline_suspended", QuotaMessage(recovery)); }
        store.UpdateSettings(s => s with { LastAutomaticOffline = clock() });
        result = await portal.AuthenticateAsync(secret, settings.Carrier, token);
        if (!result.Success) SuspendAutoOffline(); return result;
    }
    private static string QuotaMessage(string reason) => reason switch { "captcha" => "设备处理：自助平台要求验证码，请打开自助页面处理。", "no_candidate" => "设备处理：没有可确认的名单外候选，请查看在线设备和保护名单。", "reconnected" => "设备处理：目标重新连接，已停止自动下线。", "self_login_rejected" => "设备处理：自助登录未成功，自动提交已停止，请在自助页核对。", "uncertain" => "设备处理：结果未确认，不会自动重发，请先只读刷新。", _ => "设备处理暂不可用，请核对自助平台与设备规则。" };
    private void NetworkFault()
    {
        Volatile.Write(ref status, Status with { Authentication = "unknown", FailureAt = clock() });
        policy.ObserveOutcome(AuthenticationOutcome.TemporaryFailure, clock());
        if (Status.Phase != "unreachable") log.Write(DiagnosticCode.ServerUnavailable);
        Publish("unreachable", "network");
    }
    public async Task<AgentResponse> CommandAsync(string command, CancellationToken token)
    {
        if (command == "status") return new(true, Status);
        bool interrupt = command is "pause" or "logout";
        if (interrupt)
        {
            Interlocked.Increment(ref pauseWaiters);
            try { Volatile.Read(ref automaticCheck)?.Cancel(); } catch (ObjectDisposedException) { }
        }
        try { await gate.WaitAsync(token); }
        catch { if (interrupt) Interlocked.Decrement(ref pauseWaiters); throw; }
        try
        {
            if (command is "authenticate_once" or "authenticate_resume" or "reauthenticate" or "authenticate_guarded")
            {
                var settings = store.ReadSettings(); var network = wifi();
                if (command == "authenticate_guarded")
                {
                    string blocked = settings.Memory.PausedByTool ? "paused" : settings.Memory.CredentialsRejected ? "campus_credentials" :
                        settings.CredentialIssue is "quota" or "carrier_state" or "campus_missing" or "carrier_account" or "carrier_quota" or "business_rejected" or "auto_offline_suspended" ? settings.CredentialIssue : "";
                    if (blocked.Length != 0) return new(false, Status with
                    {
                        Phase = blocked == "paused" ? "paused" : "fault",
                        Issue = blocked,
                        Paused = settings.Memory.PausedByTool,
                        CheckedAt = clock()
                    }, blocked);
                    command = "authenticate_once";
                }
                Volatile.Write(ref status, Status with { KeepAliveEnabled = settings.Enabled });
                if (network.Kind != "nufe") { Publish("off_campus", nufe: false); return new(false, Status, "off_campus"); }
                if (!settings.HttpConsent) return new(false, Status, "consent_required");
                var secret = store.ReadSecret(); if (secret == null) return new(false, Status, "no_credentials");
                if (!store.MatchesActiveProfile(settings, secret)) return new(false, Status, "profile_transition");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(55));
                var current = await portal.ReadAsync(deadline.Token);
                Volatile.Write(ref status, Status with { CurrentAccount = current.Account, Authentication = current.Online ? "online" : "offline", IsNufe = true });
                if (current.Online && command == "reauthenticate")
                {
                    var previous = portal.Matches(current, secret.Account, settings.Carrier) ? new AccountProfile("", secret, settings.Carrier, settings) : store.FindOnlineProfile(current.Account);
                    if (previous == null || !portal.Matches(current, previous.Campus.Account, previous.Carrier)) { Publish("fault", "different_account"); return new(false, Status, "different_account"); }
                    policy.PauseFromTool(); Remember(); await portal.LogoutAsync(previous.Campus, previous.Carrier, deadline.Token);
                    current = await portal.ReadAsync(deadline.Token);
                    if (current.Online) { Publish("fault", "unknown_response"); return new(false, Status, "logout_unconfirmed_paused"); }
                }
                if (current.Online)
                {
                    bool matches = portal.Matches(current, secret.Account, settings.Carrier);
                    if (matches && command == "authenticate_resume") { policy.ResumeFromTool(); Remember(); }
                    Publish(matches ? "online" : "fault", matches ? "" : "different_account"); return new(matches, Status, matches ? "ok" : "different_account");
                }
                Publish("authenticating"); var result = await AuthenticateTarget(secret, settings, deadline.Token);
                if (result.Success)
                {
                    policy.CredentialsUpdated(); if (command != "authenticate_once") policy.ResumeFromTool();
                    policy.ObserveOutcome(AuthenticationOutcome.ConfirmedOnline, clock()); Remember();
                    store.UpdateSettings(s => s with { CredentialIssue = "", LastNotifiedIssue = "" });
                    Volatile.Write(ref status, Status with { CurrentAccount = secret.Account + settings.Carrier, Authentication = "online", LastRecovery = clock(), ServerMessage = "", FailureAt = null });
                    Publish("online");
                }
                else
                {
                    Volatile.Write(ref status, Status with { ServerMessage = result.ServerMessage, FailureAt = clock() });
                    if (result.Code is "campus_credentials" or "carrier_credentials")
                    {
                        policy.ObserveOutcome(result.Code == "carrier_credentials" ? AuthenticationOutcome.CarrierCredentialsRejected : AuthenticationOutcome.CampusCredentialsRejected, clock()); Remember();
                    }
                    if (result.Code != "unknown_response") store.UpdateSettings(s => s with { CredentialIssue = result.Code });
                    Publish("fault", result.Code);
                }
                return new(result.Success, Status, result.Code);
            }
            if (command == "auto_recover") { policy.ConfirmManualRecovery(); Remember(); store.UpdateSettings(s => s with { CredentialIssue = "", LastNotifiedIssue = "", LastAutomaticOffline = null }); return new(true, Status); }
            if (command == "pause" || command == "logout") { policy.PauseFromTool(); Remember(); log.Write(DiagnosticCode.Paused); Publish("paused"); }
            else if (command == "resume") { policy.ResumeFromTool(); policy.NetworkRecoveryEvent(); nextAttempt = default; Remember(); log.Write(DiagnosticCode.Resumed); }
            else if (command == "retry") { policy.ResumeFromTool(); policy.CredentialsUpdated(); Remember(); nextAttempt = default; store.UpdateSettings(s => s with { CredentialIssue = "", LastNotifiedIssue = "" }); }
            else if (command == "reload") { policy = new(store.ReadSettings().Memory); nextAttempt = default; log.Write(DiagnosticCode.CredentialsReloaded); }
            else return new(false, Status, "unsupported");
            if (command == "logout" && wifi().Kind == "nufe")
            {
                var settings = store.ReadSettings(); var secret = store.ReadSecret();
                if (secret != null && settings.HttpConsent)
                {
                    try { using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(14)); await portal.LogoutAsync(secret, settings.Carrier, limit.Token); log.Write(DiagnosticCode.UserLogout); }
                    catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or InvalidDataException or OperationCanceledException) { return new(false, Status, "logout_unconfirmed_paused"); }
                }
            }
            return new(true, Status);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { NetworkFault(); return new(false, Status, "network"); }
        catch (InvalidDataException) { Publish("fault", "unknown_response"); return new(false, Status, "unknown_response"); }
        catch (Exception error) when (error is HttpRequestException or IOException) { NetworkFault(); return new(false, Status, "network"); }
        catch (System.Security.Cryptography.CryptographicException) { Publish("fault", "credentials_unreadable"); return new(false, Status, "credentials_unreadable"); }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException) { Publish("fault", "unknown_response"); return new(false, Status, "unknown_response"); }
        finally { gate.Release(); if (interrupt) Interlocked.Decrement(ref pauseWaiters); }
    }
}
