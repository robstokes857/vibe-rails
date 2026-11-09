using Microsoft.AspNetCore.Http.Features;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;

namespace VibeRails.Routes;

/// <summary>Root-only, ordinary authenticated routes. Projects always come from the host resolver.</summary>
public static class LaunchRoutes
{
    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/v1/presets")
                && context.Request.Path.Value?.EndsWith("/attachments", StringComparison.Ordinal) == true
                && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = null;
            await next(context);
        });
        var api = app.MapGroup("/api/v1");
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (LaunchConflictException ex) { return Results.Conflict(new LaunchError(ex.Message, "revision_conflict", ex.CurrentRevision)); }
            catch (Exception ex) when (ex is LaunchValidationException or AutomationScriptValidationException or ArgumentException or BoardValidationException)
            { return Results.BadRequest(new LaunchError(ex.Message, "invalid_launch")); }
        });
        api.MapGet("/launch-catalog", async (LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.CatalogAsync(await projects.ResolveAsync(ct), ct)));
        api.MapGet("/presets", async (ILaunchStore store, CancellationToken ct) => Results.Ok(await store.ListPresetsAsync(ct)));
        api.MapGet("/actions", async (ILaunchStore store, CancellationToken ct) => Results.Ok(await store.ListActionsAsync(ct)));
        api.MapGet("/presets/{id}", async (string id, ILaunchStore store, CancellationToken ct) =>
            await store.GetPresetAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());
        api.MapPost("/presets", async (SavePresetRequest request, LaunchService service, CancellationToken ct) =>
            Results.Ok(await service.SavePresetAsync(null, request, ct)));
        api.MapPut("/presets/{id}", async (string id, SavePresetRequest request, LaunchService service, CancellationToken ct) =>
            Results.Ok(await service.SavePresetAsync(id, request, ct)));
        api.MapPost("/presets/{id}/retire", async (string id, RetireLaunchRequest request, LaunchService service, CancellationToken ct) =>
        { await service.RetireAsync("preset", id, request.ExpectedRevision, ct); return Results.NoContent(); });
        api.MapGet("/actions/{id}", async (string id, ILaunchStore store, CancellationToken ct) =>
            await store.GetActionAsync(id, ct) is { } a ? Results.Ok(a) : Results.NotFound());
        api.MapPost("/actions", async (SaveActionRequest request, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.SaveActionAsync(await projects.ResolveAsync(ct), null, request, ct)));
        api.MapPut("/actions/{id}", async (string id, SaveActionRequest request, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.SaveActionAsync(await projects.ResolveAsync(ct), id, request, ct)));
        api.MapPost("/actions/{id}/retire", async (string id, RetireLaunchRequest request, LaunchService service, CancellationToken ct) =>
        { await service.RetireAsync("action", id, request.ExpectedRevision, ct); return Results.NoContent(); });
        api.MapGet("/trigger-bindings", async (string? definitionId, ILaunchStore store, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await store.ListBindingsAsync(LaunchService.ProjectKey(await projects.ResolveAsync(ct)), definitionId, ct)));
        api.MapPost("/trigger-bindings", async (SaveBindingRequest request, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.SaveBindingAsync(await projects.ResolveAsync(ct), null, request, ct)));
        api.MapPut("/trigger-bindings/{id}", async (string id, SaveBindingRequest request, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.SaveBindingAsync(await projects.ResolveAsync(ct), id, request, ct)));
        api.MapGet("/runs", async (string? definitionId, long? before, int? limit, ILaunchStore store, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await store.ListRunsAsync(LaunchService.ProjectKey(await projects.ResolveAsync(ct)), definitionId, before, limit ?? 50, ct)));
        api.MapGet("/runs/{id}", async (string id, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            await service.GetRunAsync(await projects.ResolveAsync(ct), id, ct) is { } run ? Results.Ok(run) : Results.NotFound());
        api.MapGet("/runs/{id}/evidence", async (string id, LaunchService service, ILaunchStore store, IBoardProjectResolver projects, CancellationToken ct) =>
            await service.GetRunAsync(await projects.ResolveAsync(ct), id, ct) is not null
                ? Results.Ok(await store.GetCheckEvidenceAsync(id, ct)) : Results.NotFound());
        api.MapGet("/runs/{id}/attachments/{attachmentId}/content", async (string id, string attachmentId, ILaunchStore store, IBoardProjectResolver projects, HttpContext http, CancellationToken ct) =>
        {
            var file = await store.ReadRunAttachmentAsync(LaunchService.ProjectKey(await projects.ResolveAsync(ct)), id, attachmentId, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            return Results.File(file.Content, "application/octet-stream", file.Attachment.Name);
        });
        api.MapPost("/runs", async (LaunchRequest request, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
        {
            var project = await projects.ResolveAsync(ct);
            return Results.Ok(await service.EnqueueAsync(request.DefinitionType, request.DefinitionId,
                new(project, project, "manual", "manual:" + (request.OccurrenceId ?? Guid.NewGuid().ToString("N")),
                    PromptPrefix: request.PromptPrefix, PromptOverride: request.InitialPrompt,
                    NativeTerminal: request.NativeTerminal), ct, request.ExpectedRevision));
        });
        api.MapPost("/runs/{id}/resolve", async (string id, ResolveUncertainLaunchRequest request, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.ResolveUncertainAsync(await projects.ResolveAsync(ct), id, request.ConfirmedStopped, ct)));
        api.MapPost("/runs/{id}/retry", async (string id, LaunchService service, IBoardProjectResolver projects, CancellationToken ct) =>
            Results.Ok(await service.RetryAsync(await projects.ResolveAsync(ct), id, ct)));
        api.MapPost("/runs/{id}/cancel", async (string id, ILaunchStore store, IBoardProjectResolver projects, CancellationToken ct) =>
            await store.CancelAsync(id, LaunchService.ProjectKey(await projects.ResolveAsync(ct)), ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/presets/{id}/attachments", async (string id, ILaunchStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAttachmentsAsync(id, ct)));
        api.MapPost("/presets/{id}/attachments", async (string id, LaunchAttachmentRequest request, ILaunchStore store, CancellationToken ct) =>
        {
            return Results.Ok(await new LaunchAttachments(store).AddAsync(id, request, ct));
        });
        api.MapGet("/presets/{id}/attachments/{attachmentId}/content", async (string id, string attachmentId, ILaunchStore store, HttpContext http, CancellationToken ct) =>
        {
            var file = await store.ReadAttachmentAsync(id, attachmentId, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            return Results.File(file.Content, "application/octet-stream", file.Attachment.Name);
        });
        api.MapDelete("/presets/{id}/attachments/{attachmentId}", async (string id, string attachmentId, ILaunchStore store, CancellationToken ct) =>
            await store.RemoveAttachmentAsync(id, attachmentId, ct) ? Results.NoContent() : Results.NotFound());
    }
}
