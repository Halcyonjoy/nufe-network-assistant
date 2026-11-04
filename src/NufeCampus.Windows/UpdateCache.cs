using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NufeCampus.Windows;

// One disposable TEMP directory per attempt; never recurse into arbitrary paths or links.
public sealed class UpdateCache : IDisposable
{
    private const string Prefix = "NufeCampus-update-";
    private FileStream? lease;
    private bool handedOff;
    public string Root { get; }
    public string Installer => Path.Combine(Root, "setup.exe");
    public UpdateCache()
    {
        Root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        lease = new FileStream(Path.Combine(Root, "update.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    public static bool ValidDirectory(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(Path.GetFileName(path), "^NufeCampus-update-[0-9a-f]{32}$") &&
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    public static void CleanStale()
    {
        try
        {
            foreach (string path in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
            {
                if (!ValidDirectory(path) || Directory.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-5)) continue;
                try
                {
                    using (new FileStream(Path.Combine(path, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                    Remove(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private static void Remove(string path)
    {
        if (!ValidDirectory(path)) return;
        foreach (string name in new[] { "setup.exe", "setup.exe.partial", "update.ps1", "ready", "update.lock" }) File.Delete(Path.Combine(path, name));
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }
    public async Task StartInstallerAsync(string targetDirectory, CancellationToken token)
    {
        if (!ValidDirectory(Root) || !File.Exists(Installer)) throw new IOException("更新安装包不存在。");
        string script = Path.Combine(Root, "update.ps1");
        await File.WriteAllTextAsync(script, RunnerScript, new UTF8Encoding(true), token);
        lease?.Dispose(); lease = null;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Root };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-ParentProcessId", Environment.ProcessId.ToString(), "-TargetDirectory", Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新安装程序。");
        // The runner owns the cache before the UI exits. Cancelling this handoff leaves no installer running.
        try
        {
            for (int i = 0; i < 50; i++)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(Root, "ready"))) { handedOff = true; return; }
                if (process.HasExited) break;
                await Task.Delay(100, token);
            }
            throw new IOException("更新程序未就绪，请重试或从 Release 页面下载安装。");
        }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
    }
    public void Dispose()
    {
        lease?.Dispose(); lease = null;
        if (!handedOff) try { Remove(Root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    // PowerShell reads this script before execution, so it can remove its own script after setup exits.
    // Every removal uses LiteralPath after verifying this exact TEMP directory; exports are never touched.
    private const string RunnerScript = """
        param([int]$ParentProcessId, [string]$TargetDirectory)
        $ErrorActionPreference = 'Stop'
        $cachePath = [IO.Path]::GetFullPath($PSScriptRoot)
        $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($cachePath) -ine $tempPath -or
            [IO.Path]::GetFileName($cachePath) -cnotmatch '^NufeCampus-update-[0-9a-f]{32}$' -or
            ((Get-Item -LiteralPath $cachePath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { exit 2 }
        $cacheLock = $null
        $restart = $false
        try {
            $cacheLock = [IO.File]::Open((Join-Path $cachePath 'update.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
            [IO.File]::WriteAllText((Join-Path $cachePath 'ready'), 'ready')
            $parentProcess = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
            if ($parentProcess -and -not $parentProcess.WaitForExit(60000)) { throw 'Application did not exit' }
            $restart = $true
            $setupPath = Join-Path $cachePath 'setup.exe'
            $setupArguments = @('/SP-', '/NORESTART', '/UPDATE=1', ('/DIR="' + $TargetDirectory + '"'))
            $setupProcess = Start-Process -FilePath $setupPath -ArgumentList $setupArguments -PassThru -WindowStyle Normal
            $setupProcess.WaitForExit()
        } catch {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show('更新安装未完成。可重新打开程序检查更新，或从 GitHub Release 手动下载安装。', 'NUFE Campus') | Out-Null
        } finally {
            if ($cacheLock) { $cacheLock.Dispose() }
            foreach ($cacheName in @('setup.exe', 'setup.exe.partial', 'update.ps1', 'ready', 'update.lock')) {
                Remove-Item -LiteralPath (Join-Path $cachePath $cacheName) -Force -ErrorAction SilentlyContinue
            }
            if (-not (Get-ChildItem -LiteralPath $cachePath -Force)) {
                Remove-Item -LiteralPath $cachePath -Force -ErrorAction SilentlyContinue
            }
            $appPath = Join-Path $TargetDirectory 'NufeCampus.App.exe'
            if ($restart -and (Test-Path -LiteralPath $appPath -PathType Leaf)) {
                Start-Process -FilePath $appPath -WorkingDirectory $TargetDirectory -WindowStyle Normal
            }
        }
        """;
}
