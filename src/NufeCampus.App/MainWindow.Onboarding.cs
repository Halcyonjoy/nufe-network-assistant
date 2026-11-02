using System.IO;
using System.Windows;
using NufeCampus.Windows;

namespace NufeCampus.App;

public partial class MainWindow
{
    private SecretPair? sessionCredential;
    private string sessionCarrier = "@cmcc";

    private void InitializeCredentialChoice(bool saved)
    {
        RememberCampus.IsChecked = saved;
        if (!saved)
        {
            EnableBox.IsChecked = false;
            AutoStartBox.IsChecked = false;
            WelcomeCard.Visibility = Visibility.Visible;
            NavigateFromShortcut("settings");
            AccountBox.Focus();
            Feedback.Text = "请先填写账号，选择是否加密保存；也可以稍后设置。";
        }
        UpdateCredentialChoice();
    }

    private void CredentialChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || loadingPreferences) return;
        UpdateCredentialChoice();
        MarkUnsaved();
    }

    private void UpdateCredentialChoice()
    {
        bool remember = RememberCampus.IsChecked == true;
        EnableBox.IsEnabled = AutoStartBox.IsEnabled = AutoOfflineBox.IsEnabled = remember;
        StrategyBox.IsEnabled = CheckIntervalBox.IsEnabled = remember;
        UpdateStrategyControls();
        if (!remember)
        {
            EnableBox.IsChecked = false;
            AutoStartBox.IsChecked = false;
            AutoOfflineBox.IsChecked = false;
        }
        CredentialChoiceNote.Text = remember
            ? "保存后可在运行偏好中启用后台保活。加密保存本身不会开启保活或登录自启动。密码留空时，仅沿用同一账号已保存的密码。"
            : "仅本次使用：账号密码只保留在当前窗口内存中，关闭后失效。后台保活、自启动和自动设备处理不可用。不会删除以前保存的账号。";
        SaveButton.Content = remember ? "加密保存并应用" : "仅本次应用";
        AccountApplyButton.Content = remember ? "加密保存并应用" : "仅本次使用";
    }

    private void SkipWelcomeClick(object sender, RoutedEventArgs e) => NavigateFromShortcut("overview");

    private async Task ApplySessionCredential(string account, string password, string carrier)
    {
        if (password.Length == 0 && sessionCredential?.Account == account) password = sessionCredential.Password;
        if (password.Length == 0) throw new UserNotice("仅本次使用需要填写密码；不会读取以前保存的密码。");
        if (EnableBox.IsChecked == true || AutoStartBox.IsChecked == true || AutoOfflineBox.IsChecked == true)
            throw new UserNotice("后台功能需要加密保存账号密码，请先选择加密保存。");
        if (await AgentForConfiguration() != null)
        {
            throw new UserNotice("后台正在使用已保存的账号。请先在运行偏好中退出后台，再切换为仅本次使用。");
        }
        if (UsesScheduledKeepAlive && store.ReadSettings() is { Enabled: true, ScheduledStopped: false })
            throw new UserNotice("任务计划仍在使用已保存账号，请先停止保活，再切换为仅本次使用。");
        sessionCredential = new(account, password);
        sessionCarrier = carrier;
        CampusPassword.Clear();
        ResetSelfPage();
        WelcomeCard.Visibility = Visibility.Collapsed;
        CredentialNote.Text = "当前账号仅本次使用，未保存到本机；关闭窗口后需重新输入。";
        Feedback.Text = "账号仅本次应用。可手动认证或登录自助；后台保活需要加密保存。";
        SettingsSaved();
        NavigateFromShortcut("overview");
        await RefreshStatus();
    }

    private async Task AuthenticateSessionOnce()
    {
        var secret = sessionCredential ?? throw new UserNotice("请先填写账号密码。");
        if (HttpConsent.IsChecked != true)
        {
            NavigateFromShortcut("settings", 1);
            throw new UserNotice("认证前请阅读并确认校方 HTTP 传输说明，再点击“认证一次”。账号密码不会保存。");
        }
        if (testRoot != null)
        {
            Feedback.Text = "隔离示例：本次认证已确认，未访问校园网、未保存凭据或启动后台。";
            return;
        }
        if (Wifi.Read().Kind != "nufe") throw new UserNotice("认证仅支持 NUFE-STU；当前账号仍只保留在本窗口中。");
        using var portal = new CampusPortal();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var current = await portal.ReadAsync(timeout.Token);
        if (current.Online)
        {
            Feedback.Text = portal.Matches(current, secret.Account, sessionCarrier)
                ? "当前账号已认证。仅本次使用，不启用后台保活。"
                : "当前连接属于其他账号；未下线或替换现有连接。";
            return;
        }
        var result = await portal.AuthenticateAsync(secret, sessionCarrier, timeout.Token);
        Feedback.Text = result.Success ? "认证已确认。仅本次使用，未保存密码或启用后台保活。" : Texts.Issue(result.Code);
    }
}
