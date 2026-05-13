using VibeRails.DTOs;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;

namespace VibeRails.Routes;

public static class BertRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/bert/status", (IBertSearchDbService searchDb, IBertSettings settings) =>
        {
            return Results.Ok(BuildStatusResponse(searchDb, settings));
        }).WithName("GetBertStatus");

        app.MapGet("/api/v1/bert/captures", (IBertCaptureQueryService captures, int? skip, int? take) =>
        {
            return Results.Ok(captures.GetCaptures(skip ?? 0, take ?? BertSearchDefaults.DefaultTake));
        }).WithName("GetBertCaptures");

        app.MapGet("/api/v1/bert/session-captures", (IBertCaptureQueryService captures, int? skip, int? take) =>
        {
            return Results.Ok(captures.GetSessionCaptures(skip ?? 0, take ?? BertSearchDefaults.DefaultTake));
        }).WithName("GetBertSessionCaptures");

        app.MapGet("/api/v1/bert/captures/by-session/{sessionId}", (string sessionId, IBertCaptureQueryService captures) =>
        {
            try
            {
                return Results.Ok(captures.GetCapturesBySessionId(sessionId));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("GetBertCapturesBySession");

        app.MapGet("/api/v1/bert/captures/{documentId}", (string documentId, IBertCaptureQueryService captures) =>
        {
            try
            {
                return Results.Ok(captures.GetCapture(documentId));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("GetBertCapture");

        app.MapPost("/api/v1/bert/search", (BertSearchRequest request, IBertSearchServiceV2 bertSearch) =>
        {
            try
            {
                return Results.Ok(bertSearch.Search(request.Query, request.Mode, request.Scope, request.TopK));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("SearchBertCaptures");
    }

    private static BertStatusResponse BuildStatusResponse(IBertSearchDbService searchDb, IBertSettings settings)
    {
        var databaseExists = File.Exists(searchDb.VectorDatabasePath);
        var stateDatabaseExists = File.Exists(searchDb.StateDatabasePath);
        var modelDirectory = Path.GetDirectoryName(settings.ModelPath) ?? string.Empty;
        var modelFileExists = File.Exists(settings.ModelPath);
        var vocabFileExists = File.Exists(settings.VocabPath);
        var modelAvailable = modelFileExists && vocabFileExists;

        var documentCount = 0;
        var sessionCount = 0;
        var vectorCount = 0;
        var sessionDocumentCount = 0;
        var sessionVectorCount = 0;
        DateTime? latestCaptureUtc = null;

        if (databaseExists)
        {
            documentCount = searchDb.CountDocuments();
            sessionCount = searchDb.CountSessions();
            vectorCount = searchDb.CountVectors();
            sessionDocumentCount = searchDb.CountSessionDocuments();
            sessionVectorCount = searchDb.CountSessionVectors();

            var latestDocumentId = searchDb.GetLatestDocumentId();
            if (!string.IsNullOrWhiteSpace(latestDocumentId))
            {
                latestCaptureUtc = searchDb.GetMetadataByDocumentIds([latestDocumentId])
                    .Values
                    .FirstOrDefault()
                    ?.TimestampUtc;
            }
        }

        return new BertStatusResponse(
            databaseExists,
            stateDatabaseExists,
            modelAvailable,
            databaseExists && modelAvailable,
            settings.DataDirectory,
            searchDb.VectorDatabasePath,
            searchDb.StateDatabasePath,
            modelDirectory,
            settings.ModelPath,
            settings.VocabPath,
            documentCount,
            sessionCount,
            latestCaptureUtc,
            settings.ModelName,
            settings.EmbeddingDimension,
            settings.MaxSequenceLength,
            vectorCount,
            databaseExists ? SafeFileSize(searchDb.VectorDatabasePath) : 0,
            stateDatabaseExists ? SafeFileSize(searchDb.StateDatabasePath) : 0,
            modelFileExists ? SafeFileSize(settings.ModelPath) : 0,
            vocabFileExists ? SafeFileSize(settings.VocabPath) : 0,
            sessionDocumentCount,
            sessionVectorCount);
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
