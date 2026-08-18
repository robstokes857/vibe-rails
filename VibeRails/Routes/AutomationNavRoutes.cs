using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class AutomationNavRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/automation-nav/preferences", async (
            IAutomationNavPreferenceService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(cancellationToken)))
            .WithName("GetAutomationNavPreferences");

        app.MapPut("/api/v1/automation-nav/preferences", async (
            IAutomationNavPreferenceService service,
            UpdateAutomationNavPreferencesRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.SaveAsync(request, cancellationToken));
            }
            catch (AutomationNavPreferenceValidationException exception)
            {
                return Results.BadRequest(new ErrorResponse(exception.Message));
            }
        }).WithName("UpdateAutomationNavPreferences");

        app.MapDelete("/api/v1/automation-nav/preferences", async (
            IAutomationNavPreferenceService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ResetAsync(cancellationToken)))
            .WithName("ResetAutomationNavPreferences");
    }
}
