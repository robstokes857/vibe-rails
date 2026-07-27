namespace VibeRails.Services.Jobs;

/// <summary>
/// Compatibility tombstone for the retired <c>vb --job-tick</c> OS scheduler entry point.
/// A legacy task may still invoke it, so Program recognizes the argument and exits before Kestrel
/// starts. It intentionally performs no database, scheduling, or launch work.
/// </summary>
public static class JobTickProcessHost
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals("--job-tick", StringComparison.OrdinalIgnoreCase));

    public static Task<int> RunAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
