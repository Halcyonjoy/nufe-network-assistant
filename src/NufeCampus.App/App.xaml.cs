using System.Windows;
using NufeCampus.Windows;
namespace NufeCampus.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                MessageBox.Show("请用普通用户权限运行，以便与登录启动的后台使用同一用户权限。", "NUFE Campus"); Shutdown(2); return;
            }
            string? root = null;
            int index = Array.IndexOf(e.Args, "--test-root"); if (index >= 0 && index + 1 < e.Args.Length) root = LocalStore.ValidateTestRoot(e.Args[index + 1]);
            instance = new Mutex(true, "Local\\NufeCampusApp-" + LocalStore.UserId + LocalStore.TestSuffix(root), out bool created);
            if (!created) { MessageBox.Show("配置窗口已打开，请在任务栏切换到 NUFE Campus。", "NUFE Campus"); Shutdown(); return; }
            MainWindow = new MainWindow(new LocalStore(root), root); MainWindow.Show();
        }
        catch { MessageBox.Show("无法打开本机配置，请检查目录权限或配置文件。没有发送任何凭据。", "NUFE Campus"); Shutdown(2); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
