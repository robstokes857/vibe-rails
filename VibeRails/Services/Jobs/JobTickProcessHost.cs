using Serilog;
using VibeRails.DB;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Transient Jobs scheduler tick (<c>vb --job-tick</c>), run once a minute by an OS scheduled task.
///
/// It enqueues whatever is due, spawns a terminal per queued run, and exits — typically in well
/// under a second. Nothing stays resident between ticks, so there is no daemon to wedge, double-
/// start, or leave holding a lease. That is the whole reason for preferring it to a long-lived
/// worker: a tick that dies is simply a tick that didn't happen, and the next one is 60s away.
///
/// Deliberately does NOT start Kestrel, auth, the browser, or any hosted service. It builds the
/// three things it needs directly, the same way the post-commit hook host does.
/// </summary>
public static class JobTickProcessHost
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals("--job-tick", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How long a run may sit Queued with a spawned terminal before we call the launch failed.
    /// Generous, because a cold vb start on a slow machine is not instant — but finite, because a
    /// launch that goes nowhere (most importantly: a scheduled task with no interactive desktop)
    /// must surface as a failed run rather than as a run that quietly never happens.
    /// </summary>
    private static readonly TimeSpan LaunchGrace = TimeSpan.FromMinutes(3);

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var installDirectory = PathConstants.GetInstallDirPath();
            Directory.CreateDirectory(installDirectory);
            var statePath = Path.Combine(installDirectory, PathConstants.STATE_FILENAME);
            var connectionString = $"Data Source={statePath};Mode=ReadWriteCreate;Cache=Shared";

            // Order is not load-bearing, despite appearances: JobStore's schema declares
            // FOREIGN KEY (EnvironmentId) REFERENCES Environments(Id) before Repository has created
            // Environments, and SQLite is fine with that — a foreign key's target only has to exist
            // when the constraint is enforced, not when the table is declared.
            var store = new JobStore(connectionString);
            var repository = new Repository(connectionString);
            var launchService = new LaunchLLMService(
                new ClaudeLlmCliLauncher(),
                new CodexLlmCliLauncher(),
                new AntigravityLlmCliLauncher(),
                new CopilotLlmCliLauncher(),
                new OpencodeLlmCliLauncher());

            // Reap first. A Running row whose process is gone blocks its job's overlap guard and
            // holds a slot in the launch cap forever, so without this the queue silently wedges
            // shut after a few interrupted runs — and the dashboard, which is the only other thing
            // that reaps, is by definition closed when the OS tick is doing the work.
            var reaped = await JobRunReaper.ReapAsync(store, cancellationToken);
            if (reaped > 0)
            {
                Log.Warning("[Jobs] {Count} run(s) had no live process; marked interrupted", reaped);
            }

            var stalled = await store.FailStalledLaunchesAsync(LaunchGrace, cancellationToken);
            if (stalled > 0)
            {
                Log.Warning("[Jobs] {Count} run(s) were launched but never started; marked failed", stalled);
            }

            await store.EnqueueDueSchedulesAsync(DateTime.UtcNow, cancellationToken);

            var launcher = new JobLaunchService(store, repository, launchService);
            var launched = await launcher.LaunchQueuedRunsAsync(cancellationToken);
            if (launched > 0)
            {
                Log.Information("[Jobs] Tick launched {Count} run(s)", launched);
            }
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed tick must never be fatal — the next one is a minute away.
            Log.Error(ex, "[Jobs] Scheduler tick failed");
            return 1;
        }
    }
}
