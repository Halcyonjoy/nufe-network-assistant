namespace NufeCampus.Windows;

// Caller holds the Agent gate or RunnerLease. Never changes keepalive preferences or pause state.
public static class OneShotLogout
{
    public static async Task<AgentResponse> RunAsync(LocalStore store, ICampusPortal portal, Func<WifiState> wifi,
        CancellationToken token, SecretPair? session = null, string? sessionCarrier = null, bool? consent = null)
    {
        var settings = store.ReadSettings();
        var status = new AgentStatus
        {
            ProtocolVersion = 2,
            KeepAliveEnabled = settings.Enabled,
            Paused = settings.Memory.PausedByTool,
            HasCredentials = store.HasSecret(),
            CheckedAt = DateTimeOffset.UtcNow
        };
        bool attempted = false;
        AgentResponse Result(bool ok, string code, string phase, string authentication = "unknown") =>
            new(ok, status with { Phase = phase, Authentication = authentication, Issue = ok ? "" : code, CheckedAt = DateTimeOffset.UtcNow }, code);
        try
        {
            if (wifi().Kind != "nufe") return Result(false, "off_campus", "off_campus");
            status = status with { IsNufe = true };
            if (!(consent ?? settings.HttpConsent)) return Result(false, "consent_required", "consent_required");
            var secret = session ?? store.ReadSecret();
            if (secret == null) return Result(false, "no_credentials", "no_credentials");
            if (session == null && !store.MatchesActiveProfile(settings, secret)) return Result(false, "profile_transition", "fault");
            string carrier = session == null ? settings.Carrier : sessionCarrier ?? settings.Carrier;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(14));
            var current = await portal.ReadAsync(deadline.Token);
            if (!current.Online) return Result(true, "already_offline", "offline", "offline");
            if (!portal.Matches(current, secret.Account, carrier)) return Result(false, "different_account", "fault", "online");
            attempted = true;
            await portal.LogoutAsync(secret, carrier, deadline.Token);
            var verified = await portal.ReadAsync(deadline.Token);
            return verified.Online ? Result(false, "logout_unconfirmed", "fault", "online") : Result(true, "ok", "offline", "offline");
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        { return Result(false, attempted ? "logout_unconfirmed" : "network", "unreachable"); }
        catch (System.Security.Cryptography.CryptographicException) { return Result(false, "credentials_unreadable", "fault"); }
    }
}
