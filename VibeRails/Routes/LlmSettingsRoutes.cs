using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public static class LlmSettingsRoutes
{
    public static void Map(WebApplication app)
    {
        MapCodexSettings(app);
        MapClaudeSettings(app);
    }

    private static void MapCodexSettings(WebApplication app)
    {
        // GET /api/v1/codex/settings/{envName} - Get Codex settings for an environment
        app.MapGet("/api/v1/codex/settings/{envName}", async (
            ICodexLlmCliEnvironment codexEnv,
            string envName,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var settings = await codexEnv.GetSettings(envName, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read Codex settings: {ex.Message}"));
            }
        }).WithName("GetCodexSettings");

        // PUT /api/v1/codex/settings/{envName} - Update Codex settings for an environment
        app.MapPut("/api/v1/codex/settings/{envName}", async (
            ICodexLlmCliEnvironment codexEnv,
            string envName,
            CodexSettingsDto settings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await codexEnv.SaveSettings(envName, settings, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to save Codex settings: {ex.Message}"));
            }
        }).WithName("UpdateCodexSettings");
    }

    private static void MapClaudeSettings(WebApplication app)
    {
        // GET /api/v1/claude/settings/{envName} - Get Claude settings for an environment
        app.MapGet("/api/v1/claude/settings/{envName}", async (
            IClaudeLlmCliEnvironment claudeEnv,
            string envName,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read Claude settings: {ex.Message}"));
            }
        }).WithName("GetClaudeSettings");

        // PUT /api/v1/claude/settings/{envName} - Update Claude settings for an environment
        app.MapPut("/api/v1/claude/settings/{envName}", async (
            IClaudeLlmCliEnvironment claudeEnv,
            string envName,
            ClaudeSettingsDto settings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await claudeEnv.SaveSettings(envName, settings, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to save Claude settings: {ex.Message}"));
            }
        }).WithName("UpdateClaudeSettings");
    }
}
