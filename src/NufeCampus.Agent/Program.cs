using NufeCampus.Windows;
using System.Threading.Channels;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return 2;
        string? root = null;
        int index = Array.IndexOf(args, "--test-root"); if (index >= 0 && index + 1 < args.Length) root = LocalStore.ValidateTestRoot(args[index + 1]);
        string suffix = LocalStore.TestSuffix(root);
        using var instance = new Mutex(true, AgentLifetime.InstanceName(root), out bool acquired);
        if (!acquired) return 0;
        try
        {
            using var stop = new CancellationTokenSource();
            var store = new LocalStore(root); if (store.ReadSettings().KeepAliveStrategy != "agent") return 0;
            using var lease = RunnerLease.TryAcquire(store); if (lease == null) return 4;
            using var intervalChanged = new SemaphoreSlim(0, 1);
            var log = new Diagnostics(store.Logs);
            var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            int networkChanged = 0;
            void Pulse() { Interlocked.Exchange(ref networkChanged, 1); signals.Writer.TryWrite(true); }
            using var portal = new CampusPortal();
            using var internet = new InternetProbe();
            using var tray = new TrayHost(Pulse, stop, root);
            Func<WifiState> network = root == null ? Wifi.Read : () => new("off_campus");
            var engine = new KeepAliveEngine(store, portal, network, issue => tray.Notify(Texts.Issue(issue)), internetProbe: root == null ? internet.CheckAsync : null, quotaRecovery: root == null ? QuotaWorker.RunAsync : null);
            tray.Command = command => _ = Task.Run(async () =>
            {
                if (command == "stop") { stop.Cancel(); return; }
                try { await engine.CommandAsync(command, stop.Token); signals.Writer.TryWrite(false); } catch (OperationCanceledException) { }
            });
            var worker = Task.Run(async () =>
            {
                try { await foreach (bool _ in signals.Reader.ReadAllAsync(stop.Token)) await engine.TickAsync(Interlocked.Exchange(ref networkChanged, 0) != 0, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                finally { tray.Stop(); }
            });
            var timer = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        bool changed = await intervalChanged.WaitAsync(TimeSpan.FromSeconds(KeepAliveConfiguration.Interval(store.ReadSettings())), stop.Token);
                        if (!changed) signals.Writer.TryWrite(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
            var ipc = Task.Run(async () =>
            {
                try
                {
                    await AgentPipe.ServeAsync(LocalStore.PipeName + suffix, async (command, requestToken) =>
                    {
                        if (command == "stop") { _ = Task.Run(async () => { await Task.Delay(100); stop.Cancel(); }); return new(true, engine.Status); }
                        if (command == "query") return await OneShotAuthentication.QueryAsync(store, portal, network, requestToken);
                        var reply = await engine.CommandAsync(command, requestToken);
                        if (command == "reload" && intervalChanged.CurrentCount == 0) intervalChanged.Release();
                        if (command != "status") signals.Writer.TryWrite(false); return reply;
                    }, () => log.Write(DiagnosticCode.IpcRejected), stop.Token);
                }
                catch (OperationCanceledException) { }
            });
            using var events = new WifiEvents(Pulse);
            Pulse(); tray.Run(); stop.Cancel();
            Task.WhenAll(worker, timer, ipc).GetAwaiter().GetResult(); log.Write(DiagnosticCode.Stopped); return 0;
        }
        catch { return 2; }
        finally { instance.ReleaseMutex(); }
    }
}
