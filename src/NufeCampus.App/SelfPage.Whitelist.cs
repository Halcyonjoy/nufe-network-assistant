using NufeCampus.SelfService;
using System.Windows;
namespace NufeCampus.App;

public partial class SelfPage
{
    public event Action<string>? WhitelistUpdated;
    private void AddWhitelist(string? value)
    {
        try
        {
            if (SessionOnly) { SelfFeedback.Text = "白名单需要关联本机保存的账号。请先在校园网账号页选择加密保存；当前账号密码仍仅本次使用。"; return; }
            if (value == null) { SelfFeedback.Text = "请先选择一台设备。"; return; }
            if (QuotaRecovery.StrictMac(value) is not { } mac) { SelfFeedback.Text = "设备未提供有效 MAC，无法加入白名单。"; return; }
            if (!client.Authenticated || store.ReadSecret()?.Account != client.AuthenticatedAccount) { SelfFeedback.Text = "自助账号与当前配置账号不一致，请先载入对应账号。"; return; }
            bool added = store.AddProtectedMac(client.AuthenticatedAccount, mac); ProtectedMacsView = store.ReadSettings().ProtectedMacs; WhitelistUpdated?.Invoke(mac);
            SelfFeedback.Text = added ? "已加入当前账号白名单并保存；未启用自动处理、未下线设备。" : "该设备已在白名单中。";
        }
        catch { SelfFeedback.Text = "白名单未能保存，请检查账号是否切换或名单是否已满。"; }
    }
    private void OnlineWhitelistClick(object s, RoutedEventArgs e) => AddWhitelist((DevicesGrid.SelectedItem as OnlineDevice)?.Mac);
    private void BoundWhitelistClick(object s, RoutedEventArgs e) => AddWhitelist((BoundGrid.SelectedItem as BoundDevice)?.Mac);
}
