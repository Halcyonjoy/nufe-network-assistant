using NufeCampus.SelfService;
using NufeCampus.Windows;
if (args.Length == 2 && args[0] == "--test-root")
{
    var isolated = new LocalStore(LocalStore.ValidateTestRoot(args[1]));
    try
    {
        isolated.SaveProfile(new("14000001", "synthetic-worker"), "@cmcc", new() { AutoStart = false, SelfConsent = true, AutoOfflineEnabled = true, ProtectedMacs = ["AABBCCDDEEFF"] });
        using var fixture = new SelfClient(new WorkerFixture(), () => new("nufe", "192.0.2.1", "001122334455")) { Consent = true };
        string result = await QuotaRecovery.RunAsync(isolated, fixture, () => new("nufe", "192.0.2.1", "001122334455"), default);
        if (result != "no_candidate") return 8;
        await Task.Delay(TimeSpan.FromSeconds(12)); return 0;
    }
    finally { isolated.RemoveOwnedData(); }
}
// Short-lived worker. Secrets never enter arguments, stdout or logs. Test mode never accesses the network.
if (args.Length != 1 || args[0] != "--quota") return 2;
try
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var store = new LocalStore();
    if (!store.ReadSettings().AutoOfflineEnabled) return 2;
    using var client = new SelfClient();
    string result = await QuotaRecovery.RunAsync(store, client, Wifi.Read, stop.Token);
    return result == "freed" ? 0 : result == "captcha" ? 4 : result == "reconnected" ? 6 : 3;
}
catch (SelfException e) when (e.Code == "captcha") { return 4; }
catch (SelfException e) when (e.Code == "login") { new LocalStore().UpdateSettings(s => s with { SelfLoginBlocked = true }); return 7; }
catch { return 5; }

sealed class WorkerFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath;
        string text = path switch { "/Self/login/" => "<form method='post' action='/Self/login/verify'><input name='account'><input name='password'></form><div id='randomDiv' class='hide'></div>", "/Self/dashboard/getOnlineList" => "[]", _ => "" };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = path.EndsWith("randomCode") ? new ByteArrayContent([1]) : new StringContent(text) });
    }
}
