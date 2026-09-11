using Microsoft.AspNetCore.Http.Features;
using VibeRails.DTOs;
using VibeRails.Services.SigningKeys;

namespace VibeRails.Routes;

/// <summary>Active-root settings routes, behind the normal session + tab middleware.</summary>
public static class SigningKeyRoutes
{
    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/settings/keys"))
            {
                context.Response.Headers.CacheControl = "no-store";
                const long limit = 96 * 1024;
                if (context.Request.ContentLength > limit)
                { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
            }
            await next(context);
        });
        var group = app.MapGroup("/api/v1/settings/keys");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SigningKeyLockedException ex)
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return Results.Json(new ErrorResponse(ex.Message), AppJsonSerializerContext.Default.ErrorResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
            catch (KeyNotFoundException) { return Results.NotFound(new ErrorResponse("Signing key was not found.")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return Results.Json(new ErrorResponse("Could not access signing-key storage. Check permissions or retry when the other key operation finishes."),
                    AppJsonSerializerContext.Default.ErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        group.MapGet("", async (SigningKeyService keys, CancellationToken ct) => Results.Ok(await keys.ListAsync(ct)));
        group.MapPost("", async (CreateSigningKeyRequest request, SigningKeyService keys, CancellationToken ct) =>
            Results.Ok(await keys.CreateAsync(request, ct)));
        group.MapPost("/{id:guid}/sync", async (Guid id, SigningKeyPasswordRequest request, SigningKeyService keys, CancellationToken ct) =>
            Results.Ok(await keys.SyncAsync(id, request.Password, ct)));
        group.MapPost("/{id:guid}/export", async (Guid id, SigningKeyPasswordRequest request, SigningKeyService keys, CancellationToken ct) =>
            Results.Ok(await keys.ExportAsync(id, request.Password, ct)));
        group.MapPost("/{id:guid}/sign", async (Guid id, SignPayloadRequest request, SigningKeyService keys, CancellationToken ct) =>
            Results.Ok(await keys.SignAsync(id, request, ct)));
    }
}
