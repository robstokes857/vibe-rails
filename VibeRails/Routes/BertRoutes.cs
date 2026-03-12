using VibeRails.DTOs;
using VibeRails.Services.Bert;

namespace VibeRails.Routes;

public static class BertRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/bert/status", (IBertExplorerService bertExplorer) =>
        {
            return Results.Ok(bertExplorer.GetStatus());
        }).WithName("GetBertStatus");

        app.MapGet("/api/v1/bert/captures", (IBertExplorerService bertExplorer, int? skip, int? take) =>
        {
            return Results.Ok(bertExplorer.GetCaptures(skip ?? 0, take ?? 50));
        }).WithName("GetBertCaptures");

        app.MapGet("/api/v1/bert/captures/{documentId}", (string documentId, IBertExplorerService bertExplorer) =>
        {
            var capture = bertExplorer.GetCapture(documentId);
            return capture is null
                ? Results.NotFound(new ErrorResponse("BERT capture not found."))
                : Results.Ok(capture);
        }).WithName("GetBertCapture");

        app.MapPost("/api/v1/bert/search", (BertSearchRequest request, IBertExplorerService bertExplorer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new ErrorResponse("Provide a non-empty query."));

            try
            {
                return Results.Ok(bertExplorer.Search(request.Query, request.Mode, request.TopK));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("SearchBertCaptures");
    }
}
