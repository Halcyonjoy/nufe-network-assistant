using System.Globalization;
using System.Text.RegularExpressions;
using NufeCampus.Windows;
namespace NufeCampus.SelfService;

public static class QuotaRecovery
{
    public static string? StrictMac(string value)
    {
        value = value.Trim();
        if (!Regex.IsMatch(value, @"^(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}|(?:[0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2})$")) return null;
        string normalized = Wifi.NormalizeMac(value); return normalized is "000000000000" or "FFFFFFFFFFFF" ? null : normalized;
    }
    public static OnlineDevice? Select(IReadOnlyList<OnlineDevice> devices, Settings settings, WifiState own)
    {
        if (!settings.AutoOfflineEnabled || settings.Memory.AutoOfflineSuspended || settings.OfflineOrder == "manual" || StrictMac(own.Mac ?? "") == null || string.IsNullOrWhiteSpace(own.Address)) return null;
        var protectedMacs = settings.ProtectedMacs.Select(StrictMac).ToArray();
        if (protectedMacs.Length == 0 || protectedMacs.Any(m => m == null) || settings.OfflineOrder is not ("oldest" or "newest")) return null;
        var candidates = devices.Where(d => StrictMac(d.Mac) is { } mac && !protectedMacs.Contains(mac) && mac != StrictMac(own.Mac!) && d.Ip.Length > 0 && d.Ip != own.Address)
            .Select(d => (Device: d, Valid: DateTime.TryParse(d.LoginTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time), Time: time)).Where(d => d.Valid);
        return (settings.OfflineOrder == "oldest" ? candidates.OrderBy(d => d.Time) : candidates.OrderByDescending(d => d.Time)).ThenBy(d => d.Device.Mac, StringComparer.Ordinal).Select(d => d.Device).FirstOrDefault();
    }
    public static async Task<string> RunAsync(LocalStore store, SelfClient client, Func<WifiState> network, CancellationToken token)
    {
        var settings = store.ReadSettings(); var secret = store.ReadSecret(); var own = network();
        if (secret == null || !settings.SelfConsent || settings.SelfLoginBlocked || own.Kind != "nufe" || !settings.AutoOfflineEnabled || !store.MatchesActiveProfile(settings, secret)) return "unavailable";
        if (settings.Memory.AutoOfflineSuspended || settings.OfflineOrder == "manual" || settings.ProtectedMacs.Length == 0 || settings.ProtectedMacs.Any(m => StrictMac(m) == null) || StrictMac(own.Mac ?? "") == null || own.Address == null) return "no_candidate";
        client.Consent = true;
        var challenge = await client.PrepareLoginAsync(token); if (challenge.CaptchaRequired) return "captcha";
        await client.LoginAsync(secret, "", token);
        var selected = Select(await client.OnlineAsync(token), settings, own); if (selected == null) return "no_candidate";
        var ticket = await client.PrepareOfflineAsync(selected, token);
        var fresh = store.ReadSettings(); var current = store.ReadSecret(); var now = network();
        if (current?.Account != secret.Account || !store.MatchesActiveProfile(fresh, secret) || fresh.ActiveProfileId != settings.ActiveProfileId || fresh.Carrier != settings.Carrier || fresh.OfflineOrder != settings.OfflineOrder ||
            !fresh.ProtectedMacs.SequenceEqual(settings.ProtectedMacs) || !fresh.SelfConsent || fresh.Memory.PausedByTool != settings.Memory.PausedByTool || fresh.Memory.AutoOfflineSuspended || !fresh.AutoOfflineEnabled || now.Kind != "nufe" || now.Address != own.Address || now.Mac != own.Mac) return "changed";
        // Re-evaluate after preparation, against fresh current-account data and the exact policy.
        var candidate = Select(await client.OnlineAsync(token), fresh, now);
        if (candidate?.SessionId != selected.SessionId || candidate.Mac != selected.Mac) return "changed";
        bool StillAuthorized() { var latest = store.ReadSettings(); var identity = network(); return store.ReadSecret() == secret && store.MatchesActiveProfile(latest, secret) && latest.ActiveProfileId == settings.ActiveProfileId && latest.Carrier == settings.Carrier && latest.ProtectedMacs.SequenceEqual(settings.ProtectedMacs) && latest.OfflineOrder == settings.OfflineOrder && latest.AutoOfflineEnabled && latest.SelfConsent && !latest.Memory.AutoOfflineSuspended && latest.Memory.PausedByTool == settings.Memory.PausedByTool && identity == own; }
        await client.OfflineAsync(ticket, token, StillAuthorized);
        return (await client.OnlineAsync(token)).Any(d => StrictMac(d.Mac) == StrictMac(selected.Mac)) ? "reconnected" : "freed";
    }
}
