using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using NufeCampus.Windows;

if (args.Length == 3 && args[0] == "--verify-icon") return CustomIcon(args[1], args[2]) ? 0 : 1;
if (args.Length < 2) throw new ArgumentException("PackageChecks <setup.exe> <evidence-directory>");
string setup = Path.GetFullPath(args[0]), evidence = Path.GetFullPath(args[1]);
Directory.CreateDirectory(evidence);
string root = Path.Combine(Path.GetTempPath(), "NufeCampus-test-" + Guid.NewGuid().ToString("N"));
var store = new LocalStore(root);
string app = Path.Combine(root, "app"), testId = Path.GetFileName(root)[16..];
string task = StartupTask.Name + "-test-" + testId;
var checks = new List<(string Name, bool Passed)>();
void Check(string name, bool ok) { checks.Add((name, ok)); Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) throw new InvalidOperationException(name); }
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
async Task<(int Exit, string Output)> Run(string file, params string[] arguments)
{
    var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Launch failed");
    var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var error = process.StandardError.ReadToEndAsync(deadline.Token);
    await process.WaitForExitAsync(deadline.Token); await Task.WhenAll(output, error);
    return (process.ExitCode, await output);
}
string scheduler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
async Task<XDocument?> ReadTask(string name)
{
    var result = await Run(scheduler, "/Query", "/TN", name, "/XML");
    return result.Exit == 0 ? XDocument.Parse(result.Output) : null;
}
async Task WriteTask(XDocument xml)
{
    string file = Path.Combine(root, "fixture-task.xml");
    await File.WriteAllTextAsync(file, xml.ToString(), Encoding.Unicode);
    var result = await Run(scheduler, "/Create", "/TN", task, "/XML", file, "/F");
    if (result.Exit != 0) throw new IOException("Fixture task registration failed");
}
async Task Install(string label)
{
    var result = await Run(setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOICONS", "/TASKS=", "/DIR=" + app, "/TESTROOT=" + root, "/LOG=" + Path.Combine(evidence, label + ".log"));
    Check(label + " installer exit", result.Exit == 0);
}
async Task Uninstall(string label)
{
    var result = await Run(Path.Combine(app, "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TESTROOT=" + root, "/LOG=" + Path.Combine(evidence, label + ".log"));
    Check(label + " uninstaller exit", result.Exit == 0);
}
bool HasRegistration()
{
    using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
    if (uninstall == null) return false;
    foreach (string name in uninstall.GetSubKeyNames())
    {
        using var key = uninstall.OpenSubKey(name);
        if (string.Equals(key?.GetValue("InstallLocation")?.ToString()?.TrimEnd('\\'), app, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}
static bool CustomIcon(string file, string expectedFile)
{
    Native.ExtractIconEx(file, 0, out var large, out var small, 1);
    try
    {
        if (large == 0) return false;
        using var bitmap = Icon.FromHandle(large).ToBitmap();
        using var expectedIcon = new Icon(expectedFile, bitmap.Width, bitmap.Height);
        using var expected = expectedIcon.ToBitmap();
        int mint = 0, dark = 0, differences = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
        {
            Color c = bitmap.GetPixel(x, y);
            Color e = expected.GetPixel(x, y);
            if (Math.Abs(c.A - e.A) > 4 || Math.Abs(c.R - e.R) > 4 || Math.Abs(c.G - e.G) > 4 || Math.Abs(c.B - e.B) > 4) differences++;
            if (c.A > 200 && c.G > 130 && c.B > 110 && c.G > c.R * 1.2) mint++;
            if (c.A > 200 && c.R < 55 && c.G < 60 && c.B < 65) dark++;
        }
        return mint > bitmap.Width * bitmap.Height / 20 && dark > bitmap.Width * bitmap.Height / 3 && differences <= bitmap.Width * bitmap.Height / 100;
    }
    finally { if (large != 0) Native.DestroyIcon(large); if (small != 0 && small != large) Native.DestroyIcon(small); }
}
store.SaveSecret(new("14000001", "synthetic-installer-secret"));
store.UpdateSettings(_ => new() { Enabled = false, AutoStart = false, ProtectedMacs = ["020000000001"] });
string productionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NufeCampus");
Dictionary<string, string> HashFiles(string directory) => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    .Where(p => !Path.GetRelativePath(directory, p).StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    .ToDictionary(p => Path.GetRelativePath(directory, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))) : [];
var productionBefore = HashFiles(productionRoot);
string? productionTask = (await ReadTask(StartupTask.Name))?.ToString();
Process? agent = null;
try
{
    await Install("fresh-install");
    foreach (string name in new[] { "NufeCampus.App.exe", "NufeCampus.Agent.exe", "NufeCampus.SelfWorker.exe", "NufeCampus.SetupSupport.exe", "unins000.exe" })
        Check(name + " exists and embeds the custom icon", File.Exists(Path.Combine(app, name)) && CustomIcon(Path.Combine(app, name), Path.Combine(app, "app.ico")));
    Check("Setup executable embeds the custom icon", CustomIcon(setup, Path.Combine(app, "app.ico")));
    Check("Current-user uninstall registration exists", HasRegistration());
    Check("Fresh install does not register or enable a startup task", !await StartupTask.ExistsAsync(deadline.Token, testId));
    Check("Fresh install preserves real isolated DPAPI and whitelist", new LocalStore(root).ReadSecret()?.Password == "synthetic-installer-secret" && new LocalStore(root).ReadSettings().ProtectedMacs.SequenceEqual(["020000000001"]));
    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    var xml = XDocument.Parse(StartupTask.Xml(Path.Combine(root, "legacy", "NufeCampus.Agent.exe"), false));
    xml.Root!.Element(ns + "Settings")!.SetElementValue(ns + "Enabled", false);
    xml.Root.Element(ns + "Actions")!.Element(ns + "Exec")!.SetElementValue(ns + "Arguments", "--test-root \"" + root + "\"");
    await WriteTask(xml);
    var taskBefore = await ReadTask(task) ?? throw new IOException("Fixture missing");
    var start = new ProcessStartInfo(Path.Combine(app, "NufeCampus.Agent.exe")) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--test-root"); start.ArgumentList.Add(root);
    agent = Process.Start(start) ?? throw new IOException("Fixture agent failed");
    bool ready = false;
    for (int i = 0; i < 20; i++) { try { var reply = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(root), "status", deadline.Token); ready = reply.Status.ProcessId == agent.Id; if (ready) break; } catch (IOException) { } catch (TimeoutException) { } await Task.Delay(100); }
    Check("Installed NativeAOT agent starts in isolated off-campus mode", ready);
    await Install("upgrade");
    Check("Upgrade gracefully stops its own agent and does not restart it", agent.HasExited);
    var moved = await ReadTask(task) ?? throw new IOException("Migrated task missing");
    var exec = moved.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!;
    Check("Upgrade migrates startup executable and working directory", exec.Element(ns + "Command")?.Value == Path.Combine(app, "NufeCampus.Agent.exe") && exec.Element(ns + "WorkingDirectory")?.Value == app);
    Check("Upgrade preserves disabled state, triggers, policy and arguments", new[] { "Triggers", "Principals", "Settings" }.All(name => taskBefore.Root!.Element(ns + name)!.ToString() == moved.Root.Element(ns + name)!.ToString()) && exec.Element(ns + "Arguments")?.Value == taskBefore.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Arguments")?.Value);
    Check("Upgrade retains account, whitelist and disabled preferences", store.ReadSecret()?.Password == "synthetic-installer-secret" && store.ReadSettings().ProtectedMacs.SequenceEqual(["020000000001"]) && !store.ReadSettings().Enabled && !store.ReadSettings().AutoStart);
    exec.SetElementValue(ns + "Command", Path.Combine(root, "newer", "NufeCampus.Agent.exe")); await WriteTask(moved);
    await Uninstall("uninstall-old-copy");
    Check("Old uninstaller retains a task pointing to a newer copy", await StartupTask.ExistsAsync(deadline.Token, testId));
    Check("Uninstall removes installed app and registration", !File.Exists(Path.Combine(app, "NufeCampus.App.exe")) && !HasRegistration());
    Check("Uninstall preserves isolated credentials and whitelist", store.ReadSecret()?.Password == "synthetic-installer-secret" && store.ReadSettings().ProtectedMacs.SequenceEqual(["020000000001"]));
    await Install("reinstall");
    await Uninstall("uninstall-current-copy");
    Check("Current uninstaller removes its own startup task", !await StartupTask.ExistsAsync(deadline.Token, testId));
    var productionAfter = HashFiles(productionRoot);
    Check("Production credentials/settings are unchanged", productionBefore.Count == productionAfter.Count && productionBefore.All(p => productionAfter.TryGetValue(p.Key, out var hash) && hash == p.Value));
    Check("Production startup task is unchanged by isolated installer tests", productionTask == (await ReadTask(StartupTask.Name))?.ToString());
}
finally
{
    if (agent is { HasExited: false }) { try { await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(root), "stop", deadline.Token); } catch { } }
    agent?.Dispose();
    await StartupTask.SetAsync(false, Path.Combine(app, "NufeCampus.Agent.exe"), CancellationToken.None, testId);
    await File.WriteAllTextAsync(Path.Combine(evidence, "package-checks.json"), JsonSerializer.Serialize(new { TestRoot = root, Checks = checks.Select(c => new { c.Name, c.Passed }).ToArray(), Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed) }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"Passed {checks.Count(c => c.Passed)}, failed {checks.Count(c => !c.Passed)}");
return checks.All(c => c.Passed) ? 0 : 1;

internal static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")] internal static extern uint ExtractIconEx(string path, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
}
