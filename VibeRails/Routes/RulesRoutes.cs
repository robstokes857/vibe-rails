using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class RulesRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/rules", (IRulesService rulesService) =>
        {
            var rules = rulesService.AllowedRules();
            return Results.Ok(new AvailableRulesResponse(rules));
        }).WithName("GetAvailableRules");

        // GET /api/v1/rules/details - Get available rules with descriptions
        app.MapGet("/api/v1/rules/details", (IRulesService rulesService) =>
        {
            var rulesWithDescriptions = rulesService.AllowedRulesWithDescriptions()
                .Select(r => new RuleWithDescription(r.Name, r.Description))
                .ToList();
            return Results.Ok(new AvailableRulesWithDescriptionsResponse(rulesWithDescriptions));
        }).WithName("GetAvailableRulesWithDescriptions");
    }
}
