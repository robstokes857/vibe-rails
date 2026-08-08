using System.Net;
using VibeRails.DTOs;
using VibeRails.Services.HttpRelay;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Narrow proof surface for forwarding real local HTTP requests over the viberails.ai relay.
/// The cloud peer independently restricts the destination to JSONPlaceholder /posts.
/// </summary>
public static class HttpRelayRoutes
{
    private const string RoutePrefix = "/api/v1/http-relay/test/posts";
    private const string UpstreamBase = "https://jsonplaceholder.typicode.com/posts";

    private static readonly HashSet<string> ForwardedRequestHeaders = new(
        ["accept", "content-type"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ReturnedResponseHeaders = new(
        [
            "cache-control",
            "content-language",
            "content-type",
            "etag",
            "expires",
            "last-modified",
            "x-content-type-options"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static void Map(WebApplication app)
    {
        app.MapGet(RoutePrefix, ForwardAsync).WithName("HttpRelayTestGetPosts");
        app.MapGet(RoutePrefix + "/{id:int}", ForwardAsync).WithName("HttpRelayTestGetPost");
        app.MapPost(RoutePrefix, ForwardAsync).WithName("HttpRelayTestCreatePost");
        app.MapPut(RoutePrefix + "/{id:int}", ForwardAsync).WithName("HttpRelayTestUpdatePost");
        app.MapDelete(RoutePrefix + "/{id:int}", ForwardAsync).WithName("HttpRelayTestDeletePost");
    }

    private static async Task ForwardAsync(
        HttpContext context,
        IRemoteHttpRelayClient relayClient)
    {
        int? id = null;
        if (context.Request.RouteValues.TryGetValue("id", out var routeId))
        {
            if (!int.TryParse(routeId?.ToString(), out var parsedId))
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Post ID must be a positive integer.");
                return;
            }
            id = parsedId;
        }

        if (!ParserConfigs.GetRouteThroughVibeRailsAi())
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "HTTP relay routing is disabled in Settings.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey()))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "A VibeRails API key is required for HTTP relay routing.");
            return;
        }

        if (id is <= 0)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Post ID must be a positive integer.");
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && id is null
            && !HttpMethods.IsPost(context.Request.Method))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "This HTTP method requires a post ID.");
            return;
        }

        try
        {
            // Preserve the real incoming request shape. GET and DELETE bodies are uncommon but
            // legal at the transport layer, and this contract is intended to grow into a general
            // local proxy rather than silently discarding them.
            var bodyBytes = await ReadBodyAsync(context.Request, context.RequestAborted);

            var request = new HttpRelayRequest(
                Version: HttpRelayProtocol.Version,
                Type: HttpRelayProtocol.RequestType,
                RequestId: Guid.NewGuid().ToString("D"),
                Method: context.Request.Method.ToUpperInvariant(),
                Uri: id.HasValue ? $"{UpstreamBase}/{id.Value}" : UpstreamBase,
                Headers: CopyRequestHeaders(context.Request),
                Body: bodyBytes.Length == 0
                    ? null
                    : new HttpRelayBody("base64", Convert.ToBase64String(bodyBytes)),
                TimeoutMs: HttpRelayProtocol.MaxTimeoutMs);

            var response = await relayClient.SendAsync(request, context.RequestAborted);
            var responseBody = HttpRelayProtocol.DecodeBody(response.Body);

            context.Response.StatusCode = response.StatusCode;
            foreach (var header in response.Headers)
            {
                if (!ReturnedResponseHeaders.Contains(header.Key))
                    continue;
                try
                {
                    context.Response.Headers[header.Key] = header.Value;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // A malformed upstream header must not escape Kestrel's validation.
                }
            }
            context.Response.Headers["X-VibeRails-Relay-Elapsed-Ms"] = response.ElapsedMs.ToString();

            if (responseBody.Length > 0)
                await context.Response.Body.WriteAsync(responseBody, context.RequestAborted);
        }
        catch (HttpRelayBodyTooLargeException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (TimeoutException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, ex.Message);
        }
        catch (HttpRelayRemoteException ex)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                $"Relay error ({ex.ErrorCode}): {ex.Message}");
        }
        catch (HttpRelayException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                "The HTTP relay request timed out.");
        }
    }

    private static Dictionary<string, string[]> CopyRequestHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            if (ForwardedRequestHeaders.Contains(header.Key))
            {
                headers[header.Key.ToLowerInvariant()] = header.Value
                    .Where(static value => value is not null)
                    .Select(static value => value!)
                    .ToArray();
            }
        }
        return headers;
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > HttpRelayProtocol.MaxBodyBytes)
            throw new HttpRelayBodyTooLargeException();

        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;
            if (body.Length + count > HttpRelayProtocol.MaxBodyBytes)
                throw new HttpRelayBodyTooLargeException();
            body.Write(buffer, 0, count);
        }
        return body.ToArray();
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
            return Task.CompletedTask;

        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new ErrorResponse(message),
            AppJsonSerializerContext.Default.ErrorResponse,
            cancellationToken: context.RequestAborted);
    }

    private sealed class HttpRelayBodyTooLargeException : Exception
    {
        public HttpRelayBodyTooLargeException()
            : base($"The request body exceeds the {HttpRelayProtocol.MaxBodyBytes}-byte relay limit.")
        {
        }
    }
}
