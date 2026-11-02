using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace NufeCampus.Windows;

public sealed partial class LocalStore
{
    public static string UserId => WindowsIdentity.GetCurrent().User!.Value;
    public static string PipeName => "NufeCampus-" + UserId;
    public static string ValidateTestRoot(string root)
    {
        root = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), @"^NufeCampus-test-[0-9a-f]{32}$"))
            throw new ArgumentException("Isolated test directory required");
        return root;
    }
    public static string TestSuffix(string? root) => root == null ? "" : "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..12];
    public string Root { get; }
    public string Logs => Path.Combine(Root, "logs");
    private readonly string lockName;
    public LocalStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NufeCampus"));
        lockName = "Local\\NufeCampusStore-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Root)))[..20];
        Directory.CreateDirectory(Root);
        var sid = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false); acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, lockName);
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Local store busy");
        try { return action(); } finally { mutex.ReleaseMutex(); }
    }
    private Settings ReadSettingsUnsafe()
    {
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Settings size rejected");
        var result = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJson.Default.Settings) ?? throw new InvalidDataException("Settings invalid");
        if (result.Version != 1 || result.Carrier is not ("@cmcc" or "@njxy") || !KeepAliveConfiguration.IsValid(result.KeepAliveStrategy, result.CheckIntervalSeconds)) throw new InvalidDataException("Settings version, strategy or carrier rejected");
        if (!KeepAliveConfiguration.ValidObservation(result.ForegroundQuerySeconds, result.ForegroundProbeSeconds)) throw new InvalidDataException("Invalid foreground intervals");
        return result;
    }
    public Settings ReadSettings() => Locked(ReadSettingsUnsafe);
    public Settings UpdateSettings(Func<Settings, Settings> update) => Locked(() =>
    {
        var next = update(ReadSettingsUnsafe());
        if (next.Version != 1 || next.Carrier is not ("@cmcc" or "@njxy") || !KeepAliveConfiguration.IsValid(next.KeepAliveStrategy, next.CheckIntervalSeconds)) throw new InvalidDataException("Settings rejected");
        if (!KeepAliveConfiguration.ValidObservation(next.ForegroundQuerySeconds, next.ForegroundProbeSeconds)) throw new InvalidDataException("Invalid foreground intervals");
        AtomicWrite(Path.Combine(Root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(next, AppJson.Default.Settings));
        return next;
    });
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private string SecretPath(bool broadband) => Path.Combine(Root, broadband ? "broadband.dpapi" : "campus.dpapi");
    public bool HasSecret(bool broadband = false) => File.Exists(SecretPath(broadband));
    private Dictionary<string, SecretPair> SelfSecrets()
    {
        string path = Path.Combine(Root, "self-service.dpapi"); if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 65536) throw new CryptographicException("Self credential file size rejected");
        byte[] plain = Dpapi.Transform(File.ReadAllBytes(path), false);
        try { return JsonSerializer.Deserialize(plain, AppJson.Default.DictionaryStringSecretPair) ?? throw new CryptographicException("Self credentials invalid"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SecretPair? ReadSelfSecret(string account) => Locked(() => SelfSecrets().GetValueOrDefault(account));
    public void SaveLegacySelfSecretForMigration(SecretPair secret) => Locked(() =>
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(secret.Account, @"^\d{6,16}$") || secret.Password.Length is < 1 or > 256) throw new ArgumentException("Self credential fields invalid");
        var secrets = SelfSecrets(); secrets[secret.Account] = secret; if (secrets.Count > 100) throw new InvalidDataException("Too many self accounts");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secrets, AppJson.Default.DictionaryStringSecretPair);
        try { AtomicWrite(Path.Combine(Root, "self-service.dpapi"), Dpapi.Transform(plain, true)); } finally { CryptographicOperations.ZeroMemory(plain); }
        return true;
    });
    public void SaveSecret(SecretPair secret, bool broadband = false)
    {
        if (secret.Account.Length is < 1 or > 64 || secret.Password.Length is < 1 or > 256) throw new ArgumentException("Credential fields invalid");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secret, AppJson.Default.SecretPair);
        try { var cipher = Dpapi.Transform(plain, true); Locked(() => { AtomicWrite(SecretPath(broadband), cipher); if (broadband) UpdateVaultBroadband(secret); return true; }); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SecretPair? ReadSecret(bool broadband = false) => Locked(() =>
    {
        var path = SecretPath(broadband);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new CryptographicException("Credential file size rejected");
        var plain = Dpapi.Transform(File.ReadAllBytes(path), false);
        try { return JsonSerializer.Deserialize(plain, AppJson.Default.SecretPair) ?? throw new CryptographicException("Credential payload invalid"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    });
    public void DeleteSecret(bool broadband) => Locked(() => { File.Delete(SecretPath(broadband)); if (broadband) UpdateVaultBroadband(null); return true; });
    public void RemoveOwnedData() => Locked(() =>
    {
        foreach (var name in new[] { "campus.dpapi", "broadband.dpapi", "accounts.dpapi", "self-service.dpapi", "settings.json", "last-check.json", "runner.lock" }) File.Delete(Path.Combine(Root, name));
        foreach (var pattern in new[] { "campus.dpapi.tmp-*", "broadband.dpapi.tmp-*", "accounts.dpapi.tmp-*", "self-service.dpapi.tmp-*", "settings.json.tmp-*" }) foreach (var file in Directory.EnumerateFiles(Root, pattern)) File.Delete(file);
        if (Directory.Exists(Logs))
        {
            foreach (var file in Directory.EnumerateFiles(Logs, "*.log")) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(Logs).Any()) Directory.Delete(Logs);
        }
        if (!Directory.EnumerateFileSystemEntries(Root).Any()) Directory.Delete(Root);
        return true;
    });
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    public static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new CryptographicException("Current-user credential protection failed");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}
