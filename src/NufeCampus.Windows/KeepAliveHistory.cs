using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NufeCampus.Windows;

public enum KeepAliveSource { Agent, Scheduler }
public sealed record KeepAliveRun(int Version, string Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    KeepAliveSource Source, bool NetworkEvent, string[] Steps, string Phase, string Issue, bool AuthenticationAttempted);

// Only fixed codes enter the history: never accounts, addresses, passwords or server text.
public sealed class KeepAliveRunBuilder(KeepAliveSource source, bool networkEvent = false, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly DateTimeOffset started = (clock ?? (() => DateTimeOffset.UtcNow))();
    private readonly List<string> steps = ["started"];
    private string phase = "unconfirmed", issue = "";
    private bool attempted;
    public void Step(string code) { if (KeepAliveHistoryCodes.Steps.Contains(code) && steps.Count < 32 && steps.LastOrDefault() != code) steps.Add(code); }
    public void Observe(string state, string reason = "")
    {
        phase = KeepAliveHistoryCodes.Phases.Contains(state) ? state : "unconfirmed";
        issue = KeepAliveHistoryCodes.Issues.Contains(reason) ? reason : "unknown_response";
        if (state == "authenticating") { attempted = true; Step("authentication_requested"); }
        else if (state == "online") Step(attempted ? "authentication_confirmed" : "already_online");
        else if (state is "paused" or "disabled" or "off_campus" or "wifi_permission" or "no_credentials" or "consent_required" or "busy" or "cancelled" or "timeout") Step(state);
        else if (state == "unreachable") Step("network_error");
        else if (state == "fault") Step("needs_attention");
    }
    public KeepAliveRun Finish() => new(1, Guid.NewGuid().ToString("N"), started, now(), source, networkEvent, steps.ToArray(), phase, issue, attempted);
}

public static class KeepAliveHistoryCodes
{
    public static readonly HashSet<string> Steps = new(StringComparer.Ordinal)
    {
        "started", "process_started", "process_completed", "network_confirmed", "checking_authentication", "authentication_online", "authentication_offline",
        "authentication_requested", "authentication_confirmed", "already_online", "quota_recovery", "quota_recovery_completed", "quota_recovery_failed",
        "paused", "disabled", "off_campus", "wifi_permission", "no_credentials", "consent_required", "busy", "cancelled", "timeout", "network_error",
        "needs_attention", "retry_backoff", "unexpected_error", "strategy_changed", "forwarded_to_agent"
    };
    public static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    { "online", "offline", "paused", "disabled", "off_campus", "wifi_permission", "no_credentials", "consent_required", "busy", "cancelled", "timeout", "unreachable", "fault", "unconfirmed" };
    public static readonly HashSet<string> Issues = new(StringComparer.Ordinal)
    {
        "", "campus_credentials", "carrier_credentials", "quota", "carrier_state", "campus_missing", "carrier_account", "carrier_quota", "business_rejected",
        "auto_offline_suspended", "profile_transition", "credentials_unreadable", "different_account", "external_probe", "unknown_response", "network",
        "process_failed", "configuration_error", "strategy_changed", "stopped"
    };
    public static bool Valid(KeepAliveRun run) => run.Version == 1 && Guid.TryParseExact(run.Id, "N", out _) && Enum.IsDefined(run.Source) &&
        run.StartedAt != default && run.FinishedAt >= run.StartedAt && run.Steps is { Length: > 0 and <= 32 } &&
        run.Steps.All(Steps.Contains) && Phases.Contains(run.Phase) && Issues.Contains(run.Issue);
}

public sealed partial class LocalStore
{
    public const long KeepAliveHistoryLimit = 5 * 1024 * 1024;
    private string HistoryDirectory => Path.Combine(Root, "keepalive-history");
    private Mutex HistoryMutex() => new(false, "Local\\NufeCampusHistory-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root)))[..20]);
    private static void EnterHistory(Mutex mutex)
    {
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Keepalive history busy");
    }
    public void RecordKeepAliveRun(KeepAliveRun run)
    {
        if (!KeepAliveHistoryCodes.Valid(run)) return;
        try
        {
            using var mutex = HistoryMutex(); EnterHistory(mutex);
            try
            {
                Directory.CreateDirectory(HistoryDirectory);
                var files = Directory.EnumerateFiles(HistoryDirectory, "history-*.jsonl").Select(p => new FileInfo(p)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
                foreach (var file in files.Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)).ToArray()) { file.Delete(); files.Remove(file); }
                string day = DateTime.UtcNow.ToString("yyyyMMdd");
                var latest = files.LastOrDefault();
                string path = latest != null && latest.Name.StartsWith("history-" + day + "-", StringComparison.Ordinal) && latest.Length < 512 * 1024
                    ? latest.FullName : Path.Combine(HistoryDirectory, "history-" + day + "-" + DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(run, AppJson.Default.KeepAliveRun) + "\n", new UTF8Encoding(false));
                files = Directory.EnumerateFiles(HistoryDirectory, "history-*.jsonl").Select(p => new FileInfo(p)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
                long total = files.Sum(f => f.Length);
                foreach (var file in files) { if (total <= KeepAliveHistoryLimit) break; total -= file.Length; file.Delete(); }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* History must never cause a second authentication attempt. */ }
    }
    public KeepAliveRun[] ReadKeepAliveHistory(int limit = 300)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var mutex = HistoryMutex(); EnterHistory(mutex);
        try
        {
            if (!Directory.Exists(HistoryDirectory)) return [];
            var result = new List<KeepAliveRun>();
            foreach (var file in Directory.EnumerateFiles(HistoryDirectory, "history-*.jsonl").Select(p => new FileInfo(p)).Where(f => f.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7)).OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                if (file.Length > 600 * 1024) continue;
                foreach (string line in File.ReadAllLines(file.FullName).Reverse())
                {
                    if (line.Length > 16384) continue;
                    try
                    {
                        var run = JsonSerializer.Deserialize(line, AppJson.Default.KeepAliveRun);
                        if (run != null && KeepAliveHistoryCodes.Valid(run) && run.FinishedAt >= DateTimeOffset.UtcNow.AddDays(-7)) result.Add(run);
                    }
                    catch (JsonException) { }
                    if (result.Count == limit) return result.ToArray();
                }
            }
            return result.ToArray();
        }
        finally { mutex.ReleaseMutex(); }
    }
}
