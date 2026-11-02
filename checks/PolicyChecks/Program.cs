using NufeCampus.Core;

var now = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
var lost = new NetworkSnapshot(true, true, AuthenticationState.Offline, true);
var checks = new List<(string Name, bool Passed)>();
void Check(string name, bool passed) => checks.Add((name, passed));
Check("Off campus stays silent", new KeepAlivePolicy().Evaluate(lost with { IsNufeStu = false }, now).Work == Work.Silent);
Check("Unknown status is probed before login", new KeepAlivePolicy().Evaluate(lost with { Authentication = AuthenticationState.Unknown }, now).Work == Work.ProbeAuthentication);
Check("Internet fault while authenticated does not reauthenticate", new KeepAlivePolicy().Evaluate(lost with { Authentication = AuthenticationState.Online, InternetAvailable = false }, now).Work == Work.Wait);
Check("Self-service outage does not prevent authentication", new KeepAlivePolicy().Evaluate(lost with { SelfServiceAvailable = false }, now).Work == Work.Authenticate);
Check("Windows lock does not stop recovery", new KeepAlivePolicy().Evaluate(lost with { IsWindowsLocked = true }, now).Work == Work.Authenticate);
Check("External logout remains recoverable", new KeepAlivePolicy().Evaluate(lost, now).Work == Work.Authenticate);
Check("Server unreachable waits without a credential attempt", new KeepAlivePolicy().Evaluate(lost with { CampusServerReachable = false }, now).Work == Work.Wait);
var paused = new KeepAlivePolicy(); paused.PauseFromTool();
var restartedPaused = new KeepAlivePolicy(paused.Memory); restartedPaused.NetworkRecoveryEvent();
Check("Tool pause survives restart and wake events", restartedPaused.Evaluate(lost, now).Reason == Reason.PausedByUser);
restartedPaused.ResumeFromTool();
Check("Explicit resume enables recovery", restartedPaused.Evaluate(lost, now).Work == Work.Authenticate);
foreach (var failure in new[] { AuthenticationOutcome.CampusCredentialsRejected, AuthenticationOutcome.CarrierCredentialsRejected })
{
    var rejected = new KeepAlivePolicy(); rejected.ObserveOutcome(failure, now); rejected.NetworkRecoveryEvent();
    rejected = new KeepAlivePolicy(rejected.Memory);
    Check(failure + " stays blocked across events and restart", rejected.Evaluate(lost, now.AddMinutes(30)).Work == Work.RequestCredentialRepair);
    rejected.CredentialsUpdated();
    Check(failure + " can recover after credential correction", rejected.Evaluate(lost, now.AddMinutes(30)).Work == Work.Authenticate);
}
var stopped = new KeepAlivePolicy(); stopped.StopThisRun();
Check("Explicit quit stops this run only", stopped.Evaluate(lost, now).Work == Work.Silent && new KeepAlivePolicy(stopped.Memory).Evaluate(lost, now).Work == Work.Authenticate);
var retry = new KeepAlivePolicy(); retry.ObserveOutcome(AuthenticationOutcome.TemporaryFailure, now);
Check("Transient failure backs off, then permits recovery", retry.Evaluate(lost, now.AddSeconds(4)).Reason == Reason.RetryDelay && retry.Evaluate(lost, now.AddSeconds(5)).Work == Work.Authenticate);
retry.NetworkRecoveryEvent();
Check("New network event removes only transient delay", retry.Evaluate(lost, now).Work == Work.Authenticate);
var corrected = new KeepAlivePolicy(); corrected.ObserveOutcome(AuthenticationOutcome.TemporaryFailure, now);
corrected.ObserveOutcome(AuthenticationOutcome.CarrierCredentialsRejected, now); corrected.CredentialsUpdated();
Check("Corrected credentials can be tested without an old retry delay", corrected.Evaluate(lost, now).Work == Work.Authenticate);
var device = new KeepAlivePolicy(); device.SuspendAutomaticDeviceOffline();
device = new KeepAlivePolicy(device.Memory); device.NetworkRecoveryEvent();
Check("Automatic device offline suspension persists until manual recovery", device.Memory.AutoOfflineSuspended);
device.ConfirmManualRecovery(); Check("Confirmed manual recovery clears device suspension", !device.Memory.AutoOfflineSuspended);
foreach (var check in checks) Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}");
Console.WriteLine($"Passed {checks.Count(c => c.Passed)}, failed {checks.Count(c => !c.Passed)}");
return checks.All(c => c.Passed) ? 0 : 1;
