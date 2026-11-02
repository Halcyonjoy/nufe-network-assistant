using System.Security.Principal;
using System.Text.Json;
using NufeCampus.Windows;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length != 1 || args[0] is not ("--status" or "--authenticate"))
        {
            Console.Error.WriteLine("用法：NufeCampus.Cli.exe --status | --authenticate"); return 2;
        }
        try
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.Error.WriteLine("请使用保存账号的普通 Windows 用户运行，不要以管理员身份运行。"); return 2;
            }
            // Test isolation is deliberately not a third public command-line argument.
            string? root = RuntimeScope.TestRoot();
            var store = new LocalStore(root);
            bool scheduled = Environment.GetEnvironmentVariable("NUFE_CAMPUS_SCHEDULED") == "1";
            bool Stopped() => store.ReadSettings() is var s && (s.KeepAliveStrategy != "scheduler" || !s.Enabled || s.ScheduledStopped);
            if (scheduled && Stopped()) return 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(58));
            AgentResponse reply;
            if (AgentLifetime.IsRunning(root))
            {
                // Never fall back to a competing runner when a live Agent cannot answer.
                reply = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(root),
                    args[0] == "--status" ? "query" : "authenticate_guarded", timeout.Token);
            }
            else
            {
                using var lease = RunnerLease.TryAcquire(store);
                if (lease == null) { Console.Error.WriteLine("认证或配置正在处理中，本次未重复执行。"); return 4; }
                if (scheduled && Stopped()) return 0;
                using var portal = new CampusPortal();
                Func<WifiState> network = root == null ? Wifi.Read : () => new("off_campus");
                if (args[0] == "--status") reply = await OneShotAuthentication.QueryAsync(store, portal, network, timeout.Token);
                else
                {
                    var engine = new KeepAliveEngine(store, portal, network, _ => { }, quotaRecovery: root == null ? QuotaWorker.RunAsync : null);
                    if (scheduled)
                    {
                        if (store.ReadSettings().ScheduledStopped)
                        {
                            reply = new(false, engine.Status with { Phase = "disabled", CheckedAt = DateTimeOffset.UtcNow }, "stopped");
                            Console.WriteLine(JsonSerializer.Serialize(reply, AppJson.Default.AgentResponse)); return 1;
                        }
                        await engine.TickAsync(false, timeout.Token);
                        var status = engine.Status;
                        reply = new(status.Phase is "online" or "off_campus", status, status.Issue.Length > 0 ? status.Issue : status.Phase);
                        store.SaveLastCheck(status);
                    }
                    else
                    {
                        reply = await OneShotAuthentication.AuthenticateAsync(engine, store, timeout.Token);
                        if (store.ReadSettings().KeepAliveStrategy == "scheduler") store.SaveLastCheck(reply.Status);
                    }
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(reply, AppJson.Default.AgentResponse));
            if (reply.Status.Phase == "off_campus") return 3;
            return reply.Ok ? 0 : 1;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("操作超时，结果未确认；不会自动重复提交。"); return 5; }
        catch { Console.Error.WriteLine("操作未完成，请检查本机配置、Wi-Fi 权限和脱敏诊断。"); return 1; }
    }
}
