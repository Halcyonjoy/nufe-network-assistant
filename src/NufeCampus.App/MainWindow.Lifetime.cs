using System.ComponentModel;
using System.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private bool closeAllowed;

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closeAllowed || removed) return;
        if (confirmation != null) { e.Cancel = true; return; }
        if (busy || selfPage?.IsWriting == true)
        {
            e.Cancel = true;
            Feedback.Text = "操作仍在进行，请先等待完成或取消请求，再关闭窗口。";
            return;
        }
        if (!hasUnsavedChanges && !observationUnsaved && !taskNameUnsaved) return;
        e.Cancel = true;
        if (await ConfirmAsync("放弃未应用的设置？", "未应用的账号、密码或设置会丢失。关闭此窗口不会停止已经运行的后台。确认关闭？"))
        {
            closeAllowed = true;
            Close();
        }
    }

    private async void ExitAllClick(object sender, RoutedEventArgs e)
    {
        if (busy || removed || confirmation != null || selfPage?.IsWriting == true)
        {
            Feedback.Text = "请先等待或取消当前操作，再退出程序及后台。";
            return;
        }
        if (!await ConfirmAsync("退出程序及后台", (hasUnsavedChanges || observationUnsaved ? "未应用的设置会被放弃。\n" : "") +
            (UsesScheduledKeepAlive ? "将关闭窗口并停止任务计划保活，不注销校园网。任务不会在下一分钟重新启动；需手动启动保活或保存应用来恢复。确认退出？" :
            "将关闭窗口并退出本次保活后台，不注销校园网连接。下次 Windows 登录仍按自启动偏好运行。确认退出？"))) return;
        await Run(async () =>
        {
            await StopAgent();
            closeAllowed = true;
            Close();
        });
    }
}
