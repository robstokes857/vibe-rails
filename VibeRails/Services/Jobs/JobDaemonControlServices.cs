using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Serilog;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;

namespace VibeRails.Services.Jobs;

public interface IJobDaemonKicker
{
    /// <summary>
    /// Best-effort wakeup only. Durable SQLite queue rows remain authoritative when VBD is absent.
    /// </summary>
    Task KickAsync(CancellationToken cancellationToken = default);
}

internal sealed class JobDaemonKicker(
    IDaemonControlClient controlClient,
    ICurrentUserIdentityProvider identityProvider,
    Func<CurrentUserIdentity, bool>? daemonMayBeRunning = null) : IJobDaemonKicker
{
    private static readonly TimeSpan KickTimeout = TimeSpan.FromMilliseconds(350);
    private readonly Func<CurrentUserIdentity, bool> _daemonMayBeRunning =
        daemonMayBeRunning ?? DefaultDaemonPresenceProbe;

    // Identity resolution is deliberately lazy and inside KickAsync's try: in profile-less
    // environments it can throw, and that must degrade to a skipped best-effort wakeup instead
    // of failing DI construction of every service that optionally depends on the kicker.
    private CurrentUserIdentity? _identity;
    private string? _pipeName;

    public async Task KickAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var identity = _identity ??= identityProvider.GetCurrent();
            _pipeName ??= JobDaemonRegistrationFactory.GetPipeName(identity);

            // The pipe connect burns its full 350ms timeout when no daemon exists — the opt-in
            // default — and callers such as Run-now and the commit hook sit on that latency.
            // A microsecond instance-guard probe answers "is any VBD process alive?" first.
            if (!_daemonMayBeRunning(identity))
            {
                Log.Debug("[VBD] Scheduler wakeup skipped: no VBD process holds the current-user instance guard");
                return;
            }

            var result = await controlClient.SendAsync(
                _pipeName,
                DaemonControlCommand.Kick,
                KickTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome is not (DaemonControlClientOutcome.Success or DaemonControlClientOutcome.Unreachable))
            {
                Log.Debug(
                    "[VBD] Scheduler wakeup was not accepted. outcome={Outcome} error={Error}",
                    result.Outcome,
                    result.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Queue persistence already succeeded. A cancelled best-effort wakeup is harmless.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[VBD] Best-effort scheduler wakeup failed");
        }
    }

    private static bool DefaultDaemonPresenceProbe(CurrentUserIdentity identity)
    {
        try
        {
            return DaemonInstanceGuard.IsHeld(
                JobDaemonRegistrationFactory.ApplicationId,
                identity,
                VibeRails.Utils.PathConstants.GetInstallDirPath(),
                attempts: 1);
        }
        catch
        {
            // A failed probe must never suppress the wakeup; fall through to the pipe attempt.
            return true;
        }
    }
}

internal static class JobDaemonWakeup
{
    public static async Task TryKickAsync(
        IJobDaemonKicker? kicker,
        CancellationToken cancellationToken = default)
    {
        if (kicker is null)
            return;

        try
        {
            await kicker.KickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A wakeup never owns the durable work and may be abandoned safely.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[VBD] Best-effort scheduler wakeup failed");
        }
    }
}

internal sealed record JobDaemonRuntimeInfo(DateTime StartedUtc)
{
    public static JobDaemonRuntimeInfo Create() => new(DateTime.UtcNow);
}

internal sealed record JobDaemonControlStatusPayload(
    string Version,
    int ProtocolVersion,
    int Pid,
    DateTime StartedUtc,
    double UptimeSeconds,
    DateTime? LastCycleUtc,
    bool OwnsSchedulerLease,
    string? LastError);

internal sealed class JobDaemonControlHandler(
    IJobScheduler scheduler,
    JobSchedulerHealth health,
    JobDaemonRuntimeInfo runtime,
    IHostApplicationLifetime applicationLifetime) : IDaemonControlHandler
{
    public ValueTask<DaemonControlHandlerResult> HandleAsync(
        DaemonControlCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return command switch
        {
            DaemonControlCommand.Ping => ValueTask.FromResult(
                DaemonControlHandlerResult.Ok($"VibeRails Demon {VersionInfo.Version}")),
            DaemonControlCommand.Status => ValueTask.FromResult(Status()),
            DaemonControlCommand.Kick => ValueTask.FromResult(Kick()),
            DaemonControlCommand.Shutdown => ValueTask.FromResult(Shutdown()),
            _ => ValueTask.FromResult(DaemonControlHandlerResult.Fail("Unsupported daemon command."))
        };
    }

    private DaemonControlHandlerResult Status()
    {
        var snapshot = health.GetSnapshot();
        var payload = new JobDaemonControlStatusPayload(
            VersionInfo.Version,
            DaemonControlProtocol.Version,
            Environment.ProcessId,
            runtime.StartedUtc,
            Math.Max(0, (DateTime.UtcNow - runtime.StartedUtc).TotalSeconds),
            // Liveness, not ownership: a VBD contended out of the lease by an open dashboard
            // still cycles healthily, and LastCycleCompletedUtc advances on contended and failed
            // cycles alike. LastSuccessfulCycleUtc would sit at "Not reported" forever there,
            // indistinguishable from a wedged scheduler. Failures still surface via LastError.
            snapshot.LastCycleCompletedUtc,
            snapshot.OwnsSchedulerLease,
            snapshot.LastError);
        var element = JsonSerializer.SerializeToElement(
            payload,
            JobDaemonControlJsonContext.Default.JobDaemonControlStatusPayload);
        return DaemonControlHandlerResult.Ok(payload: element);
    }

    private DaemonControlHandlerResult Kick()
    {
        scheduler.Kick();
        return DaemonControlHandlerResult.Ok("Scheduler wakeup queued.");
    }

    private DaemonControlHandlerResult Shutdown() => DaemonControlHandlerResult.Ok(
        "Graceful shutdown requested.",
        afterResponse: () =>
        {
            Log.Information("[VBD] Graceful shutdown requested through the current-user control pipe");
            applicationLifetime.StopApplication();
            return ValueTask.CompletedTask;
        });
}

internal sealed class JobDaemonControlHostedService(
    IDaemonControlHandler handler,
    ICurrentUserIdentityProvider identityProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = JobDaemonRegistrationFactory.GetPipeName(identityProvider);
        Log.Information("[VBD] Current-user control pipe listening. protocol={Protocol}", DaemonControlProtocol.Version);
        try
        {
            await new DaemonControlServer(pipeName, handler).RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            Log.Information("[VBD] Current-user control pipe stopped");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JobDaemonControlStatusPayload))]
internal sealed partial class JobDaemonControlJsonContext : JsonSerializerContext;
