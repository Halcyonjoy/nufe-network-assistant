using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private async void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("移除本机数据", "移除当前用户的自启动任务、本机配置、加密凭据和诊断日志。手动导出的备份和记录不删除。程序文件可随后自行删除。确认清理？")) return;
        await Run(async () =>
        {
            await StopAgent(); if (testRoot == null) { using var limit = new CancellationTokenSource(10000); await StartupTask.SetAsync(false, AgentExe, limit.Token, taskName: store.ReadSettings().TaskName); }
            await Task.Run(store.RemoveOwnedData); removed = true; refresh.Stop(); CampusPassword.Clear();
            Feedback.Text = "本机配置、凭据和自启动已清理。手动导出文件保留，可关闭窗口。"; PhaseText.Text = "本机数据已移除"; IssueText.Text = "";
        });
    }
    private string? ExportPath(string title, string filter, string filename)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = filename, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return null;
        string path = Path.GetFullPath(dialog.FileName);
        if (path.Equals(store.Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(store.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new UserNotice("请选择工具数据目录以外的位置，以免备份随清理受影响。");
        return path;
    }
    private async void BackupClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        string? path = ExportPath("导出不含凭据的备份", "JSON 文件|*.json", "NUFE-偏好备份.json"); if (path == null) { Feedback.Text = "已取消。"; return; }
        var settings = store.ReadSettings(); var backup = new PreferencesBackup(1, settings.Enabled, settings.AutoStart, settings.Carrier, settings.KeepAliveStrategy, settings.CheckIntervalSeconds, settings.ForegroundQuerySeconds, settings.ForegroundProbeSeconds);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(backup, AppJson.Default.PreferencesBackup), closed.Token); Feedback.Text = "偏好已导出，文件不包含任何凭据。";
    });
    private async void ImportClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "JSON 备份|*.json", Title = "导入不含凭据的偏好" }; if (dialog.ShowDialog(this) != true) { Feedback.Text = "已取消。"; return; }
        if (new FileInfo(dialog.FileName).Length > 65536) throw new UserNotice("备份文件过大。");
        var backup = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(dialog.FileName, closed.Token), AppJson.Default.PreferencesBackup);
        if (backup == null || backup.Version != 1 || backup.Carrier is not ("@cmcc" or "@njxy") || !KeepAliveConfiguration.IsValid(backup.KeepAliveStrategy, backup.CheckIntervalSeconds) || !KeepAliveConfiguration.ValidObservation(backup.ForegroundQuerySeconds, backup.ForegroundProbeSeconds)) throw new UserNotice("备份版本或内容不支持。");
        EnableBox.IsChecked = backup.Enabled; AutoStartBox.IsChecked = backup.AutoStart; CarrierBox.SelectedIndex = backup.Carrier == "@cmcc" ? 0 : 1;
        StrategyBox.SelectedIndex = backup.KeepAliveStrategy == "scheduler" ? 1 : 0; CheckIntervalBox.Text = backup.CheckIntervalSeconds.ToString(); UpdateStrategyControls();
        ForegroundQueryBox.Text = backup.ForegroundQuerySeconds.ToString(); ForegroundProbeBox.Text = backup.ForegroundProbeSeconds.ToString();
        Feedback.Text = "偏好已载入，请检查后保存并应用。凭据及 HTTP 确认不会从备份导入。";
    });
    private async void DiagnosticClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        string? path = ExportPath("导出脱敏诊断", "ZIP 文件|*.zip", "NUFE-脱敏诊断.zip"); if (path == null) { Feedback.Text = "已取消。"; return; }
        AgentStatus snapshot; try { snapshot = (await Call("status")).Status; } catch { snapshot = new() { Phase = "stopped" }; }
        await Task.Run(() =>
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write); using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using (var output = archive.CreateEntry("status.json").Open()) JsonSerializer.Serialize(output, snapshot with { CurrentAccount = "", ServerMessage = "" }, AppJson.Default.AgentStatus);
            using var text = new StreamWriter(archive.CreateEntry("events.log").Open(), Encoding.UTF8);
            if (Directory.Exists(store.Logs)) foreach (var log in Directory.EnumerateFiles(store.Logs, "*.log").Select(p => new FileInfo(p)).Where(f => f.Length <= 1024 * 1024).OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
            {
                foreach (string line in File.ReadLines(log.FullName))
                {
                    string[] parts = line.Split(' ', 2); if (parts.Length == 2 && DateTimeOffset.TryParse(parts[0], out var time) && Enum.TryParse<DiagnosticCode>(parts[1], out var code) && Enum.IsDefined(code)) text.WriteLine(time.ToString("O") + " " + code);
                }
            }
        }); Feedback.Text = "脱敏诊断已导出，不含账号、IP、MAC、密码或令牌。";
    });
}
