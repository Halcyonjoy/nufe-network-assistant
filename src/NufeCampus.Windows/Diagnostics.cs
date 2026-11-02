using System.Text;
namespace NufeCampus.Windows;

public enum DiagnosticCode { Started, Stopped, Paused, Resumed, CredentialsReloaded, AuthConfirmed, AuthRejected, ServerUnavailable, ProtocolUnknown, IpcRejected, ConfigurationRejected, UserLogout, WifiPermission, QuotaRequired }
public sealed class Diagnostics(string directory)
{
    public const long Limit = 10 * 1024 * 1024;
    private readonly object gate = new();
    private string? active;
    public void Write(DiagnosticCode code)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                Prune();
                if (active == null || !File.Exists(active) || new FileInfo(active).Length > 512 * 1024)
                    active = Path.Combine(directory, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".log");
                File.AppendAllText(active, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("O") + " " + code + Environment.NewLine, Encoding.UTF8);
                Prune();
            }
            catch (IOException) { /* A diagnostic failure never prompts a credential retry. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
    public void Prune()
    {
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, "*.log").Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        foreach (var file in files.Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)).ToArray()) { file.Delete(); files.Remove(file); }
        long total = files.Sum(f => f.Length);
        foreach (var file in files) { if (total <= Limit) break; total -= file.Length; file.Delete(); }
    }
}
