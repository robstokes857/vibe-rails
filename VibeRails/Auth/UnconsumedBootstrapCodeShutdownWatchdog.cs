using Serilog;
using VibeRails.Utils;

namespace VibeRails.Auth;

public interface IUnconsumedBootstrapCodeShutdownWatchdog
{
    void Start();
}

public sealed class UnconsumedBootstrapCodeShutdownWatchdog(
    IAuthService authService,
    IHostApplicationLifetime hostApplicationLifetime) : IUnconsumedBootstrapCodeShutdownWatchdog
{
    public void Start()
    {
        if (!authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out var expiryUtc))
        {
            return;
        }

        // Each call arms a fresh watcher tied to the expiry it observed. If a newer
        // bootstrap code is generated later, the prior watcher's TryExpire... call
        // will see a different _bootstrapCodeExpiry and skip shutdown.
        _ = WatchAsync(expiryUtc);
    }

    private async Task WatchAsync(DateTime expiryUtc)
    {
        try
        {
            var delay = expiryUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, hostApplicationLifetime.ApplicationStopping);
            }

            // Atomic check-and-mark closes the race where a user could authenticate
            // between a separate "is unconsumed?" check and StopApplication().
            if (!authService.TryExpireUnconsumedBootstrapCode(expiryUtc))
            {
                Log.Information(
                    "[Lifecycle] Bootstrap code watchdog skipped shutdown (already consumed or superseded). expiryUtc={ExpiryUtc:O} processId={ProcessId}",
                    expiryUtc,
                    Environment.ProcessId);
                return;
            }

            var detail = $"expiryUtc={expiryUtc:O}; processId={Environment.ProcessId}; reason=bootstrap_code_unused";
            ShutdownDiagnostics.RecordStopRequest("UnconsumedBootstrapCodeWatchdog", detail);
            Log.Warning(
                "[Lifecycle] Bootstrap code was not consumed before expiry. Stopping process {ProcessId}. expiryUtc={ExpiryUtc:O}",
                Environment.ProcessId,
                expiryUtc);
            hostApplicationLifetime.StopApplication();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Lifecycle] Failed to run unconsumed bootstrap code shutdown watchdog");
        }
    }
}
