using System.Diagnostics;

namespace NufeCampus.Windows;

public static class AgentLifetime
{
    public static string InstanceName(string? root) => "Global\\NufeCampusAgent-" + LocalStore.UserId + LocalStore.TestSuffix(root);

    public static bool IsRunning(string? root)
    {
        if (!Mutex.TryOpenExisting(InstanceName(root), out var instance)) return false;
        instance.Dispose();
        return true;
    }

    public static async Task StopAsync(string? root, string executable, CancellationToken token)
    {
        if (root != null) LocalStore.ValidateTestRoot(root);
        AgentResponse reply;
        try { reply = await AgentPipe.CallAsync(LocalStore.PipeName + LocalStore.TestSuffix(root), "stop", token); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            if (!IsRunning(root)) return;
            throw new IOException("Agent stop could not be confirmed", error);
        }
        if (!reply.Ok || reply.Status.ProcessId <= 0) throw new IOException("Agent stop was not acknowledged");
        try
        {
            using var process = Process.GetProcessById(reply.Status.ProcessId);
            if (!string.Equals(process.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Agent process identity mismatch");
            await process.WaitForExitAsync(token);
        }
        catch (ArgumentException) { /* The acknowledged process has already exited. */ }
        if (IsRunning(root)) throw new IOException("Agent is still running");
    }
}
