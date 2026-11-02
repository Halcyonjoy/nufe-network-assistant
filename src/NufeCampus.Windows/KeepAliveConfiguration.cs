namespace NufeCampus.Windows;

public static class KeepAliveConfiguration
{
    public static bool ValidObservation(int querySeconds, int probeSeconds) =>
        querySeconds is >= 1 and <= 3600 && probeSeconds is >= 5 and <= 86400;
    public static bool IsValid(string strategy, int seconds) =>
        strategy is "agent" or "scheduler" && seconds >= (strategy == "scheduler" ? 60 : 10) &&
        seconds <= 86400 && (strategy != "scheduler" || seconds % 60 == 0);

    public static int Interval(Settings settings) => IsValid(settings.KeepAliveStrategy, settings.CheckIntervalSeconds)
        ? settings.CheckIntervalSeconds : throw new InvalidDataException("Invalid keepalive strategy or interval");
}

public static class RuntimeScope
{
    public static string? TestRoot()
    {
        string? explicitRoot = Environment.GetEnvironmentVariable("NUFE_CAMPUS_TEST_ROOT");
        if (explicitRoot != null) return LocalStore.ValidateTestRoot(explicitRoot);
        // An isolated installed task has no extra public arguments. Infer only the validated
        // TEMP/NufeCampus-test-<guid>/app layout, never a general adjacent configuration file.
        string directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string? parent = Path.GetDirectoryName(directory);
        if (Path.GetFileName(directory).Equals("app", StringComparison.OrdinalIgnoreCase) && parent != null &&
            Path.GetFileName(parent).StartsWith("NufeCampus-test-", StringComparison.Ordinal))
            return LocalStore.ValidateTestRoot(parent);
        return null;
    }
}

// The OS releases this handle on crashes. The file remains as an empty lock marker.
// Configuration changes and short-lived runners cannot race account changes or an Agent.
public static class RunnerLease
{
    public static FileStream? TryAcquire(LocalStore store)
    {
        try { return new FileStream(Path.Combine(store.Root, "runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    public static async Task<FileStream> AcquireAsync(LocalStore store, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquire(store) is { } lease) return lease;
            await Task.Delay(100, token);
        }
    }
}
