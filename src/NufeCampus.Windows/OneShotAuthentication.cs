using System.Text.Json;

namespace NufeCampus.Windows;

public static class OneShotAuthentication
{
    // Read-only observation does not decrypt credentials, authenticate, or update pause/error state.
    public static async Task<AgentResponse> QueryAsync(LocalStore store, ICampusPortal portal, Func<WifiState> wifi, CancellationToken token)
    {
        var settings = store.ReadSettings(); var network = wifi();
        var status = new AgentStatus
        {
            ProtocolVersion = 2,
            CheckedAt = DateTimeOffset.UtcNow,
            KeepAliveEnabled = settings.Enabled,
            Paused = settings.Memory.PausedByTool,
            HasCredentials = store.HasSecret(),
            IsNufe = network.Kind == "nufe",
            Phase = network.Kind == "permission" ? "wifi_permission" : "off_campus"
        };
        if (!status.IsNufe) return new(network.Kind != "permission", status, status.Phase);
        // This endpoint returns state only; no password or HTTP credential consent is needed.
        try
        {
            var current = await portal.ReadAsync(token);
            return new(true, status with
            {
                Phase = current.Online ? "online" : "offline",
                Authentication = current.Online ? "online" : "offline",
                CurrentAccount = current.Account
            });
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or JsonException)
        {
            return new(false, status with { Phase = "unreachable", Issue = "network" }, "network");
        }
    }

    public static Task<AgentResponse> AuthenticateAsync(KeepAliveEngine engine, LocalStore store, CancellationToken token) =>
        engine.CommandAsync("authenticate_guarded", token);
}

public sealed partial class LocalStore
{
    public AgentStatus? ReadLastCheck() => Locked(() =>
    {
        string path = Path.Combine(Root, "last-check.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Status too large");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJson.Default.AgentStatus);
    });
    public void SaveLastCheck(AgentStatus status) => Locked(() =>
    {
        AtomicWrite(Path.Combine(Root, "last-check.json"), JsonSerializer.SerializeToUtf8Bytes(
            status with { ProcessId = 0, CurrentAccount = "", ServerMessage = "" }, AppJson.Default.AgentStatus));
        return true;
    });
}
