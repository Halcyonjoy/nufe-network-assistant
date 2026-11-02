namespace NufeCampus.Core;

public enum AuthenticationState { Unknown, Offline, Online }
public enum Work { Silent, Wait, ProbeAuthentication, Authenticate, RequestCredentials, RequestCredentialRepair }
public enum Reason { OffCampus, Disabled, StoppedThisRun, PausedByUser, NetworkUnavailable, UnknownAuthentication, AlreadyOnline, NoCredentials, CredentialsRejected, RetryDelay, AuthenticationLost }
public enum AuthenticationOutcome { ConfirmedOnline, CampusCredentialsRejected, CarrierCredentialsRejected, TemporaryFailure }

public sealed record PolicyMemory(bool PausedByTool = false, bool CredentialsRejected = false, bool AutoOfflineSuspended = false);
public sealed record NetworkSnapshot(bool IsNufeStu, bool CampusServerReachable, AuthenticationState Authentication,
    bool HasCredentials, bool SelfServiceAvailable = true, bool IsWindowsLocked = false, bool InternetAvailable = true);
public sealed record Decision(Work Work, Reason Reason, TimeSpan? CheckAgainAfter = null);

/// <summary>Decides work only. Network IO, secret storage and Windows scheduling belong to separate adapters.</summary>
public sealed class KeepAlivePolicy(PolicyMemory? memory = null)
{
    public PolicyMemory Memory { get; private set; } = memory ?? new();
    private bool stoppedThisRun;
    private int temporaryFailures;
    private DateTimeOffset retryAfter;

    public void PauseFromTool() => Memory = Memory with { PausedByTool = true };
    public void ResumeFromTool() => Memory = Memory with { PausedByTool = false };
    public void StopThisRun() => stoppedThisRun = true;
    public void CredentialsUpdated()
    {
        Memory = Memory with { CredentialsRejected = false };
        NetworkRecoveryEvent();
    }
    public void SuspendAutomaticDeviceOffline() => Memory = Memory with { AutoOfflineSuspended = true };
    public void ConfirmManualRecovery() => Memory = Memory with { AutoOfflineSuspended = false };

    public void ObserveOutcome(AuthenticationOutcome outcome, DateTimeOffset now)
    {
        if (outcome is AuthenticationOutcome.CampusCredentialsRejected or AuthenticationOutcome.CarrierCredentialsRejected)
        {
            Memory = Memory with { CredentialsRejected = true };
            return;
        }
        if (outcome == AuthenticationOutcome.ConfirmedOnline) { temporaryFailures = 0; retryAfter = default; return; }
        temporaryFailures = Math.Min(temporaryFailures + 1, 4);
        retryAfter = now + TimeSpan.FromSeconds(temporaryFailures switch { 1 => 5, 2 => 15, 3 => 30, _ => 60 });
    }

    // A real network/resume event creates a new recovery window; it never clears a user pause or credential error.
    public void NetworkRecoveryEvent() { temporaryFailures = 0; retryAfter = default; }

    public Decision Evaluate(NetworkSnapshot network, DateTimeOffset now, bool enabled = true)
    {
        if (!network.IsNufeStu) return new(Work.Silent, Reason.OffCampus);
        if (!enabled) return new(Work.Silent, Reason.Disabled);
        if (stoppedThisRun) return new(Work.Silent, Reason.StoppedThisRun);
        if (Memory.PausedByTool) return new(Work.Wait, Reason.PausedByUser);
        if (!network.CampusServerReachable) return new(Work.Wait, Reason.NetworkUnavailable);
        if (network.Authentication == AuthenticationState.Online) return new(Work.Wait, Reason.AlreadyOnline);
        if (network.Authentication == AuthenticationState.Unknown) return new(Work.ProbeAuthentication, Reason.UnknownAuthentication);
        if (Memory.CredentialsRejected) return new(Work.RequestCredentialRepair, Reason.CredentialsRejected);
        if (!network.HasCredentials) return new(Work.RequestCredentials, Reason.NoCredentials);
        if (now < retryAfter) return new(Work.Wait, Reason.RetryDelay, retryAfter - now);
        return new(Work.Authenticate, Reason.AuthenticationLost);
    }
}
