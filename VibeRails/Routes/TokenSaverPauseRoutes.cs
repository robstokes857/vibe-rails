using System.Globalization;
using Serilog;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmProxy;

namespace VibeRails.Routes;

/// <summary>
/// The agent-facing control surface for the token saver: pause compression for a few minutes,
/// resume it, or ask what state it is in. Driven by the MCP tools in
/// <c>Services/Mcp/Tools/TokenSaverTool.cs</c>.
///
/// These live under <c>/llm/</c> rather than <c>/api/</c> deliberately. The process that must honor
/// the pause is the one hosting the proxy — the terminal tab's own child <c>vb.exe</c> — and the
/// only address a CLI-spawned tool knows for that process is the proxy base URL it was launched
/// with. Sharing the prefix means one URL and one auth contract
/// (<see cref="LlmProxyAuthGateExtensions.IsRequestAuthenticated"/>: session token <b>and</b> tab
/// token) reach both.
///
/// Handlers are plain <see cref="RequestDelegate"/>s to match the proxy routes: this assembly is
/// published Native AOT, where the minimal-API request-delegate generator does not run for every
/// shape, and the proxy path has already settled on explicit delegates for that reason.
/// </summary>
public static class TokenSaverPauseRoutes
{
    public const string ControlPathPrefix = "/llm/control/token-saver";

    public static void Map(WebApplication app)
    {
        app.MapPost(ControlPathPrefix + "/pause", Handle(pause: true)).WithName("TokenSaverPause");
        app.MapPost(ControlPathPrefix + "/resume", Handle(pause: false)).WithName("TokenSaverResume");
        app.MapGet(ControlPathPrefix + "/status", Handle(pause: null)).WithName("TokenSaverStatus");
    }

    private static RequestDelegate Handle(bool? pause) => async context =>
    {
        var services = context.RequestServices;
        var authGate = services.GetRequiredService<ILlmProxyAuthGate>();
        if (!authGate.IsRequestAuthenticated(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        // Read before mutating. Deliberately the pause-blind view: while paused, the effective
        // snapshot reports every saver off, so "paused" and "switched off in Settings" would be
        // indistinguishable and an agent pausing a saver that was never running would be told its
        // output is about to change.
        var saverEnabled = services.GetRequiredService<ITokenSaverConfiguration>().IsConfiguredOn();

        var pauseState = services.GetRequiredService<ITokenSaverPauseState>();
        var before = pauseState.PausedUntilUtc;

        switch (pause)
        {
            case true when saverEnabled:
                pauseState.Pause();
                break;

            // Pausing a saver that is not switched on would start a real window behind a response
            // that truthfully reports nothing was paused — and the meter hides its badge in that
            // state, so nothing on screen would mention it either. Switching the saver on inside
            // that window would then suppress compression with no visible cause. There is nothing
            // to pause, so pause nothing and let the response say so.
            case true:
                break;

            // Resume always runs, even with the saver switched off: a window opened while it was on
            // has to stay clearable afterwards.
            case false:
                pauseState.Resume();
                break;

            default:
                break; // status: read-only.
        }

        var pausedUntil = pauseState.PausedUntilUtc;

        // Publish only on a real transition. A status poll must not repaint the meter, and a
        // second pause inside an existing window is a restart the browser already accounts for
        // (it holds the absolute expiry), so re-announcing it is still worth doing — but a
        // resume-when-not-paused is not.
        if (pause is not null && before != pausedUntil)
        {
            services.GetRequiredService<IAppEventBus>()
                .PublishTokenSaverPause(pausedUntil, saverEnabled);

            Log.Information(
                "[TokenSaver] Compression {Action} via MCP control. pausedUntilUtc={PausedUntil} saverEnabled={SaverEnabled} processId={ProcessId}",
                pausedUntil is null ? "resumed" : "paused",
                pausedUntil?.ToString("o", CultureInfo.InvariantCulture) ?? "(none)",
                saverEnabled,
                Environment.ProcessId);
        }

        await Results
            .Ok(new TokenSaverPausePayload(
                pausedUntil?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                saverEnabled))
            .ExecuteAsync(context);
    };
}
