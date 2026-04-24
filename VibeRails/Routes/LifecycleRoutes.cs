using Serilog;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class LifecycleRoutes
{
    private const string BrowserOwnerPrefix = "browser:";
    private static readonly TimeSpan BrowserPulseTtl = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan BrowserCloseShutdownDelay = TimeSpan.FromMinutes(1);
    private static int _browserCloseShutdownRequested;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/lifecycle/ping", (
            HttpContext context,
            ILocalClientTracker localClientTracker) =>
        {
            var ownerId = GetBrowserOwnerId(context);
            if (ownerId == null)
            {
                return Results.BadRequest(new ErrorResponse("clientId is required"));
            }

            localClientTracker.PulseOwner(ownerId, BrowserPulseTtl);
            return Results.NoContent();
        }).WithName("LifecyclePing");

        app.MapPost("/api/v1/lifecycle/disconnect", (
            HttpContext context,
            ILocalClientTracker localClientTracker,
            IHostApplicationLifetime hostApplicationLifetime) =>
        {
            var ownerId = GetBrowserOwnerId(context);
            if (ownerId == null)
            {
                return Results.BadRequest(new ErrorResponse("clientId is required"));
            }

            localClientTracker.ReleaseOwner(ownerId);
            ScheduleBrowserCloseShutdownIfNeeded(context, localClientTracker, hostApplicationLifetime, ownerId);
            return Results.NoContent();
        }).WithName("LifecycleDisconnect");

        app.MapPost("/api/v1/shutdown", (HttpContext context, IHostApplicationLifetime hostApplicationLifetime) =>
        {
            var detail =
                $"path={context.Request.Path}; clientId={context.Request.Query["clientId"].FirstOrDefault() ?? "n/a"}; remoteIp={context.Connection.RemoteIpAddress}; userAgent={context.Request.Headers.UserAgent.ToString()}";
            ShutdownDiagnostics.RecordStopRequest("LifecycleRoutes.Shutdown", detail);
            Log.Warning("[Lifecycle] Explicit shutdown requested. {Detail}", detail);

            // Delay stop very slightly so the HTTP response can be flushed.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                hostApplicationLifetime.StopApplication();
            });

            return Results.Ok(new MessageResponse("Shutdown requested"));
        }).WithName("ShutdownHost");
    }

    private static string? GetBrowserOwnerId(HttpContext context)
    {
        var raw = context.Request.Query["clientId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 128)
        {
            return null;
        }

        return $"{BrowserOwnerPrefix}{trimmed}";
    }

    private static void ScheduleBrowserCloseShutdownIfNeeded(
        HttpContext context,
        ILocalClientTracker localClientTracker,
        IHostApplicationLifetime hostApplicationLifetime,
        string ownerId)
    {
        var args = ParserConfigs.GetArguments();
        if (!args.ShutdownOnBrowserClose || args.IsLMBootstrap || args.IsVsCodeMode)
        {
            return;
        }

        var detailPrefix =
            $"ownerId={ownerId}; remoteIp={context.Connection.RemoteIpAddress}; userAgent={context.Request.Headers.UserAgent.ToString()}";

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BrowserCloseShutdownDelay);

                if (localClientTracker.HasActiveOwnersWithPrefix(BrowserOwnerPrefix))
                {
                    Log.Information(
                        "[Lifecycle] Browser disconnect ignored because another browser owner is active. {Detail}",
                        detailPrefix);
                    return;
                }

                if (Interlocked.Exchange(ref _browserCloseShutdownRequested, 1) == 1)
                {
                    return;
                }

                var ownerSummary = localClientTracker.GetDiagnosticsSummary();
                var detail = $"{detailPrefix}; owners={ownerSummary}";
                ShutdownDiagnostics.RecordStopRequest("LifecycleRoutes.BrowserDisconnected", detail);
                Log.Warning(
                    "[Lifecycle] Last browser disconnected in --web mode. Stopping process {ProcessId}. {Detail}",
                    Environment.ProcessId,
                    detail);
                hostApplicationLifetime.StopApplication();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Lifecycle] Failed to process browser disconnect shutdown");
            }
        });
    }
}
