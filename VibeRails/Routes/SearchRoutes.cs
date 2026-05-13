using VibeRails.DTOs;
using VibeRails.Services.BertV2;

namespace VibeRails.Routes;

public static class SearchRoutes
{
    public static void Map(WebApplication app)
    {
        // Unified search: runs per-message semantic, per-session semantic, and lexical
        // searches against the same query and returns all three groups so the UI can
        // surface every lens at once. Reuses one query embedding across the two
        // semantic groups.
        app.MapPost("/api/v1/search", (UnifiedSearchRequest request, IUnifiedSearchService unifiedSearch) =>
        {
            try
            {
                return Results.Ok(unifiedSearch.Search(request.Query, request.TopK));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("UnifiedSearch");
    }
}
