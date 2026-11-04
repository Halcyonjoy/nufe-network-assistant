using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private ReleaseUpdate? availableUpdate;
    private CancellationTokenSource? updateCancellation;
    private void InitializeUpdates()
    {
        CurrentVersionText.Text = "当前版本：" + ReleaseUpdates.CurrentVersion + " · Windows x64";
        if (testRoot == null) _ = Task.Run(UpdateCache.CleanStale);
        Closed += (_, _) => updateCancellation?.Cancel();
    }
    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (updateCancellation != null || busy || removed) return;
        availableUpdate = null; InstallUpdateButton.IsEnabled = false; CheckUpdateButton.IsEnabled = false;
        UpdateNotes.Text = ""; UpdateStatus.Text = "正在检查 GitHub 最新正式版本…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30)); updateCancellation = cancellation;
        CancelUpdateButton.IsEnabled = true;
        try
        {
            using var client = ReleaseUpdates.CreateClient();
            var release = await Task.Run(() => new ReleaseUpdates(client).CheckAsync(cancellation.Token), cancellation.Token);
            UpdateNotes.Text = release.Notes;
            if (release.Version > ReleaseUpdates.CurrentVersion)
            {
                availableUpdate = release; InstallUpdateButton.IsEnabled = true;
                UpdateStatus.Text = $"发现新版本 {release.Version} · 安装包 {release.Size / 1048576d:0.#} MB";
            }
            else UpdateStatus.Text = release.Version == ReleaseUpdates.CurrentVersion ? "当前已是最新正式版本。" : "当前版本高于已发布的正式版本，无需更新。";
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "检查已取消或超时，可重试或打开 Release 页面。"; }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
        { UpdateStatus.Text = error is InvalidDataException ? error.Message : "检查更新未完成，请稍后重试或打开 Release 页面。"; }
        finally { updateCancellation = null; CheckUpdateButton.IsEnabled = true; CancelUpdateButton.IsEnabled = false; }
    }
    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } release || updateCancellation != null || busy || removed) return;
        if (testRoot != null) { UpdateStatus.Text = "隔离预览中不启动实际安装程序。"; return; }
        if (hasUnsavedChanges || observationUnsaved || taskNameUnsaved || selfPage?.IsWorking == true)
        { UpdateStatus.Text = "请先保存或放弃未应用的设置，并等待自助操作结束。"; return; }
        if (!await ConfirmAsync("安装更新", $"下载并安装 {release.Version}？校验通过后将关闭当前窗口并打开安装程序；安装结束后重新打开程序。账号配置和保活策略保留，安装与升级不会主动执行保活任务。")) return;
        if (busy || removed || updateCancellation != null) return;
        await Run(async () =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(10)); updateCancellation = cancellation;
            CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false; CancelUpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Visible; UpdateProgress.Value = 0;
            try
            {
                using var cache = new UpdateCache(); using var client = ReleaseUpdates.CreateClient();
                var progress = new Progress<int>(value => { UpdateProgress.Value = value; UpdateStatus.Text = $"正在下载 {release.Version}：{value}%"; });
                await Task.Run(() => new ReleaseUpdates(client).DownloadAsync(release, cache.Installer, progress, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (hasUnsavedChanges || observationUnsaved || taskNameUnsaved || selfPage?.IsWorking == true)
                    throw new UserNotice("检测到新的未保存设置或自助操作，请处理后重新更新。");
                await cache.StartInstallerAsync(AppContext.BaseDirectory, cancellation.Token);
                closeAllowed = true; Close();
            }
            catch (OperationCanceledException) { UpdateStatus.Text = "更新已取消或超时，下载临时文件已清理。"; }
            catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { UpdateStatus.Text = error is InvalidDataException ? error.Message : "更新未完成，临时文件已清理。请重试或从 Release 页面手动下载。"; }
            finally
            {
                updateCancellation = null; CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
                CancelUpdateButton.IsEnabled = false; UpdateProgress.Visibility = Visibility.Collapsed;
            }
        });
    }
    private void CancelUpdateClick(object sender, RoutedEventArgs e) => updateCancellation?.Cancel();
    private void OpenReleaseClick(object sender, RoutedEventArgs e) => OpenUrl(availableUpdate?.Page ?? "https://github.com/Halcyonjoy/nufe-network-assistant/releases/latest");
}
