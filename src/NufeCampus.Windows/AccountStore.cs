using System.Security.Cryptography;
using System.Text.Json;
namespace NufeCampus.Windows;

public sealed partial class LocalStore
{
    public bool AddProtectedMac(string account, string mac) => Locked(() =>
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(mac, @"^[A-F0-9]{12}$")) throw new ArgumentException("Invalid MAC");
        var secret = ReadSecret(); var settings = ReadSettingsUnsafe();
        if (secret == null || secret.Account != account || !MatchesActiveProfile(settings, secret)) throw new InvalidOperationException("Account changed");
        var list = settings.ProtectedMacs.ToList(); if (list.Contains(mac)) return false; if (list.Count >= 100) throw new InvalidDataException("Whitelist full"); list.Add(mac);
        var next = UpdateSettings(s => s with { ProtectedMacs = list.ToArray() });
        WriteVault(ReadVault().Profiles.Select(p => p.Id == next.ActiveProfileId ? p with { Preferences = p.Preferences with { ProtectedMacs = next.ProtectedMacs } } : p)); return true;
    });
    public SecretPair? SharedCredential() => Locked(() =>
    {
        var current = ReadSecret(); if (current == null) return null;
        // Migrate only the selected account's previously verified local login.
        var legacy = ReadSelfSecret(current.Account);
        if (legacy != null) { SaveSharedCredential(legacy); return legacy; }
        return current;
    });
    public void SaveSharedCredential(SecretPair secret) => Locked(() =>
    {
        var current = ReadSecret(); if (current == null || current.Account != secret.Account) throw new InvalidDataException("Select this account on the authentication page first");
        SaveSecret(secret);
        WriteVault(ReadVault().Profiles.Select(p => p.Campus.Account == secret.Account ? p with { Campus = secret } : p));
        var old = SelfSecrets(); if (old.Remove(secret.Account))
        {
            string path = Path.Combine(Root, "self-service.dpapi");
            if (old.Count == 0) File.Delete(path);
            else
            {
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(old, AppJson.Default.DictionaryStringSecretPair);
                try { AtomicWrite(path, Dpapi.Transform(plain, true)); } finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        UpdateSettings(s => s with { SelfLoginBlocked = false }); return true;
    });
    // Entire account index, including account names and per-account MAC policies, is DPAPI encrypted.
    private AccountVault ReadVault()
    {
        string path = Path.Combine(Root, "accounts.dpapi");
        if (!File.Exists(path)) return new(1, []);
        if (new FileInfo(path).Length > 1_000_000) throw new CryptographicException("Vault size rejected");
        byte[] plain = Dpapi.Transform(File.ReadAllBytes(path), false);
        try
        {
            var vault = JsonSerializer.Deserialize(plain, AppJson.Default.AccountVault);
            if (vault == null || vault.Version != 1 || vault.Profiles.Length > 50) throw new CryptographicException("Vault invalid");
            return vault;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void WriteVault(IEnumerable<AccountProfile> profiles)
    {
        var rows = profiles.ToArray();
        if (rows.Length > 50) throw new InvalidDataException("At most 50 local profiles");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new AccountVault(1, rows), AppJson.Default.AccountVault);
        try { AtomicWrite(Path.Combine(Root, "accounts.dpapi"), Dpapi.Transform(plain, true)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public AccountChoice[] Accounts() => Locked(() => ReadVault().Profiles.Select(p => new AccountChoice(p.Id, p.Campus.Account, p.Carrier)).ToArray());
    public AccountProfile? FindOnlineProfile(string account) => Locked(() => ReadVault().Profiles.SingleOrDefault(p => p.Campus.Account + p.Carrier == account));
    public bool MatchesActiveProfile(Settings settings, SecretPair secret) => Locked(() => settings.ActiveProfileId.Length == 0 || ReadVault().Profiles.Any(p => p.Id == settings.ActiveProfileId && p.Campus.Account == secret.Account && p.Carrier == settings.Carrier));
    private void UpdateVaultBroadband(SecretPair? secret)
    {
        string id = ReadSettingsUnsafe().ActiveProfileId; if (id.Length == 0 || id == "transition" || !File.Exists(Path.Combine(Root, "accounts.dpapi"))) return;
        var profiles = ReadVault().Profiles.Select(p => p.Id == id ? p with { Broadband = secret } : p).ToArray(); WriteVault(profiles);
    }
    private List<AccountProfile> SnapshotCurrent(bool? previousPaused = null)
    {
        var list = ReadVault().Profiles.ToList(); var current = ReadSecret(); var settings = ReadSettingsUnsafe();
        if (current != null)
        {
            var existing = list.Find(p => p.Campus.Account == current.Account);
            string id = existing?.Id ?? Guid.NewGuid().ToString("N");
            list.RemoveAll(p => p.Id == id);
            var saved = settings with { ActiveProfileId = id, Memory = previousPaused is { } paused ? settings.Memory with { PausedByTool = paused } : settings.Memory };
            list.Add(new(id, current, settings.Carrier, saved, ReadSecret(true)));
        }
        return list;
    }
    public void SaveProfile(SecretPair secret, string carrier, Settings preferences, bool? previousPaused = null) => Locked(() =>
    {
        if (carrier is not ("@cmcc" or "@njxy")) throw new InvalidDataException("Carrier rejected");
        var list = SnapshotCurrent(previousPaused); var previous = list.Find(p => p.Campus.Account == secret.Account);
        string id = previous?.Id ?? Guid.NewGuid().ToString("N");
        var active = preferences with { ActiveProfileId = id, Carrier = carrier };
        list.RemoveAll(p => p.Id == id); list.Add(new(id, secret, carrier, active, previous?.Broadband));
        UpdateSettings(s => s with { ActiveProfileId = "transition" }); WriteVault(list); SaveSecret(secret); UpdateSettings(_ => active); return true;
    });
    public void SelectProfile(string id, bool? previousPaused = null) => Locked(() =>
    {
        var list = SnapshotCurrent(previousPaused); var profile = list.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Profile not found");
        var global = ReadSettingsUnsafe();
        UpdateSettings(s => s with { ActiveProfileId = "transition" }); WriteVault(list); SaveSecret(profile.Campus);
        if (profile.Broadband == null) DeleteSecret(true); else SaveSecret(profile.Broadband, true);
        // Preserve global startup/HTTP consent choices, but isolate account policy and credential failures.
        UpdateSettings(_ => profile.Preferences with
        {
            ActiveProfileId = profile.Id,
            Carrier = profile.Carrier,
            Enabled = global.Enabled,
            AutoStart = global.AutoStart,
            KeepAliveStrategy = global.KeepAliveStrategy,
            CheckIntervalSeconds = global.CheckIntervalSeconds,
            ForegroundQuerySeconds = global.ForegroundQuerySeconds,
            ForegroundProbeSeconds = global.ForegroundProbeSeconds,
            ScheduledStopped = global.ScheduledStopped,
            HttpConsent = global.HttpConsent,
            SelfConsent = global.SelfConsent
        });
        return true;
    });
}
