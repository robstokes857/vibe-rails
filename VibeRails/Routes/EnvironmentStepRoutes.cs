using System.Text.Json;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Cli;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Everything step-shaped that is not just a field on an environment.
///
/// CRUD deliberately lives on the environment endpoints (steps are a child collection, exactly
/// like Jobs/JobTriggers); the one thing that needs its own route is the streaming test run, which
/// executes a command right now and renders its output in the editor.
///
/// SECURITY: the test endpoint executes arbitrary shell commands as the user. It sits behind the
/// ordinary CookieAuthMiddleware path — the skip list is frozen at bootstrap/health/OPTIONS per
/// API_SEC.md §1 and nothing here is added to it.
/// </summary>
public static class EnvironmentStepRoutes
{
    /// <summary>Same cap HostShellCommandService applies to a host-shell command.</summary>
    public const int MaxCommandChars = 24_000;

    /// <summary>Enough for a real setup chain, few enough that a runaway list can't be saved.</summary>
    public const int MaxStepsPerEnvironment = 20;

    public static void Map(WebApplication app)
    {
        // POST /api/v1/environments/steps/test — run one command and stream its output.
        // Hidden and captured (not a visible window) because the output has to render in the page,
        // but built by the same TerminalScriptBuilder a real run uses so PATH, profile loading, and
        // exit-code capture cannot diverge between "it passed its test" and "it ran at launch".
        app.MapPost("/api/v1/environments/steps/test", async (
            HttpContext context,
            ICliWrapper cli,
            TestEnvironmentStepRequest request) =>
        {
            var cancellationToken = context.RequestAborted;

            var commandError = ValidateCommand(request?.Command);
            if (commandError != null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                var errorJson = JsonSerializer.Serialize(
                    new ErrorResponse(commandError),
                    AppJsonSerializerContext.Default.ErrorResponse);
                await context.Response.WriteAsync(errorJson, cancellationToken);
                return;
            }

            var workingDirectory = string.IsNullOrWhiteSpace(request!.WorkingDirectory)
                ? ParserConfigs.GetRootPath()
                : request.WorkingDirectory!;
            var timeoutSeconds = ClampTimeout(request.TimeoutSeconds);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Append("X-Accel-Buffering", "no");
            await context.Response.StartAsync(cancellationToken);

            var linesWritten = 0;

            async ValueTask WriteEventAsync(EnvironmentStepTestEvent testEvent)
            {
                if (context.RequestAborted.IsCancellationRequested)
                    return;

                var json = JsonSerializer.Serialize(
                    testEvent,
                    AppJsonSerializerContext.Default.EnvironmentStepTestEvent);
                try
                {
                    await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // The browser intentionally closed the stream (Cancel/navigation).
                }
            }

            try
            {
                using var script = TerminalScriptBuilder.CreateCapturedScript(request.Command, workingDirectory);

                var result = await cli.RunAsync(
                    new CliRequest(
                        script.Executable,
                        script.Arguments,
                        workingDirectory,
                        Timeout: TimeSpan.FromSeconds(timeoutSeconds)),
                    async line =>
                    {
                        linesWritten++;
                        await WriteEventAsync(new EnvironmentStepTestEvent("line", line.Text, line.IsError));
                    },
                    cancellationToken);

                // A failure that happened before the process started (missing working directory,
                // executable not found) never produced a line, so its explanation is only in the
                // captured buffer. Surface it rather than showing an empty console.
                if (linesWritten == 0 && !string.IsNullOrWhiteSpace(result.StandardError))
                    await WriteEventAsync(new EnvironmentStepTestEvent("line", result.StandardError.TrimEnd(), IsError: true));

                await WriteEventAsync(new EnvironmentStepTestEvent(
                    "done",
                    ExitCode: result.ExitCode,
                    DurationMs: (long)result.Duration.TotalMilliseconds,
                    Message: result.DescribeFailure()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Browser-side Cancel aborts the request, which is the cancellation signal for the
                // child process too.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Steps] Step test run failed");
                if (context.RequestAborted.IsCancellationRequested)
                    return;

                // Converted into a terminal event rather than a broken stream, so the console can
                // render the failure instead of the reader just stopping.
                await WriteEventAsync(new EnvironmentStepTestEvent(
                    "error",
                    Message: $"Could not run the step: {ex.Message}"));
            }
        }).WithName("TestEnvironmentStep");
    }

    /// <summary>
    /// Turns a wire step list into rows to save. Returns false with a 400-able message rather than
    /// throwing: an unknown phase or an over-long command is something the user can read and fix,
    /// not a 500. Same reasoning as EnvironmentRoutes.TryParseWorkspaceMode.
    /// </summary>
    internal static bool TryBuildSteps(
        IReadOnlyList<EnvironmentStepRequest>? requests,
        out List<EnvironmentStep> steps,
        out string? error)
    {
        steps = [];
        error = null;

        if (requests is null)
            return true;

        if (requests.Count > MaxStepsPerEnvironment)
        {
            error = $"An environment can have at most {MaxStepsPerEnvironment} steps (received {requests.Count}).";
            return false;
        }

        foreach (var request in requests)
        {
            if (!Enum.IsDefined(typeof(EnvironmentStepPhase), request.Phase))
            {
                error = $"Unknown step phase: {request.Phase}. Expected 0 (before launch) or 1 (after it exits).";
                return false;
            }

            var commandError = ValidateCommand(request.Command);
            if (commandError != null)
            {
                error = commandError;
                return false;
            }

            steps.Add(new EnvironmentStep
            {
                Phase = (EnvironmentStepPhase)request.Phase,
                Name = (request.Name ?? string.Empty).Trim(),
                Command = request.Command,
                StartMinimized = request.StartMinimized,
                // Clamped rather than rejected: an out-of-range timeout is a slider the client got
                // wrong, not something the user needs to be stopped over.
                TimeoutSeconds = ClampTimeout(request.TimeoutSeconds),
                Enabled = request.Enabled
            });
        }

        return true;
    }

    /// <summary>
    /// The same two rules HostShellCommandService.ValidateRequest applies, as a 400 message rather
    /// than an exception.
    /// </summary>
    internal static string? ValidateCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "A step needs a command.";

        if (command.Length > MaxCommandChars)
            return $"Step command exceeds {MaxCommandChars} characters.";

        if (command.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
            return "Step command contains unsupported control characters.";

        return null;
    }

    internal static int ClampTimeout(int timeoutSeconds) =>
        Math.Clamp(timeoutSeconds, EnvironmentStep.MinTimeoutSeconds, EnvironmentStep.MaxTimeoutSeconds);

    internal static EnvironmentStepDto ToDto(EnvironmentStep step) =>
        new(
            step.Id,
            (int)step.Phase,
            step.Position,
            step.Name,
            step.Command,
            step.StartMinimized,
            step.TimeoutSeconds,
            step.Enabled);

    internal static List<EnvironmentStepDto> ToDtos(IEnumerable<EnvironmentStep>? steps) =>
        steps?.Select(ToDto).ToList() ?? [];
}
