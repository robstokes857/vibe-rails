using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class LlmPickerRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/llm-picker/preferences", async (
            ILlmPickerPreferenceService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(cancellationToken)))
            .WithName("GetLlmPickerPreferences");

        app.MapPut("/api/v1/llm-picker/preferences", async (
            ILlmPickerPreferenceService service,
            UpdateLlmPickerPreferencesRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.SaveAsync(request, cancellationToken));
            }
            catch (LlmPickerPreferenceValidationException exception)
            {
                return Results.BadRequest(new ErrorResponse(exception.Message));
            }
        }).WithName("UpdateLlmPickerPreferences");

        app.MapDelete("/api/v1/llm-picker/preferences", async (
            ILlmPickerPreferenceService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ResetAsync(cancellationToken)))
            .WithName("ResetLlmPickerPreferences");
    }
}
