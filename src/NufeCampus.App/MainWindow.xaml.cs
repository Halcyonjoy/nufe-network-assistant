using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly string? testRoot;
    private readonly string pipe;
    private SelfPage? selfPage;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource closed = new();
    private bool busy, refreshing, removed;
    public MainWindow(LocalStore store, string? testRoot)
    {
        InitializeComponent(); this.store = store; this.testRoot = testRoot; pipe = LocalStore.PipeName + LocalStore.TestSuffix(testRoot);
        InitializeFeatures(); InitializeShell(); InitializeKeepAliveHistory(); InitializeUpdates();
        Closing += WindowClosing;
        if (testRoot != null) { Title = "NUFE Campus · 隔离测试"; ModeNote.Text = "隔离测试实例\n不访问校园网，不修改计划任务。"; }
        Loaded += async (_, _) =>
        {
            try
            {
                await LoadPreferences(); if (closed.IsCancellationRequested) return;
                await RefreshStatus(); if (closed.IsCancellationRequested) return;
                if (WindowState != WindowState.Minimized) refresh.Start();
                await StartSelfServiceAsync();
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        };
        refresh.Tick += async (_, _) => { if (!busy && !removed) await RefreshStatus(); };
        // Async handlers may still unwind after Closed; do not dispose their token source early.
        Closed += (_, _) => { refresh.Stop(); closed.Cancel(); CampusPassword.Clear(); sessionCredential = null; };
    }
    private async Task Run(Func<Task> operation)
    {
        if (busy || removed || closed.IsCancellationRequested) return;
        busy = true; SaveButton.IsEnabled = AccountApplyButton.IsEnabled = false; MainConnectionButton.IsEnabled = false; ConnectionActions.IsEnabled = false; Feedback.Text = "正在处理…";
        try { await operation(); }
        catch (UserNotice notice) { Feedback.Text = notice.Message; }
        catch (OperationCanceledException) { if (!closed.IsCancellationRequested) Feedback.Text = "本次操作超时或取消，请先刷新状态，不要重复提交认证。"; }
        catch { Feedback.Text = "本次操作未完成。请检查配置、凭据或后台状态；不会自动重复提交。"; }
        finally { busy = false; if (!closed.IsCancellationRequested) { SaveButton.IsEnabled = AccountApplyButton.IsEnabled = !removed; ConnectionActions.IsEnabled = !removed; RenderShellStatus(latestStatus); } }
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void OpenSelfClick(object sender, RoutedEventArgs e) => OpenUrl("http://10.100.0.3:8080/Self/");
    private void OpenNativeSelfClick(object sender, RoutedEventArgs e)
    {
        NavigateFromShortcut("devices");
    }
    private void OpenPortalClick(object sender, RoutedEventArgs e) => OpenUrl("http://10.200.253.5/");
    private sealed class UserNotice(string message) : Exception(message);
}
