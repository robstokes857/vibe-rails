using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeRails.Services.HttpRelay;

/// <summary>
/// Version 1 of the buffered HTTP-over-WebSocket contract shared with viberails.ai.
/// One logical envelope is carried by one UTF-8 WebSocket text message; WebSocket frame
/// fragmentation is an implementation detail handled by the reader.
/// </summary>
public static class HttpRelayProtocol
{
    public const int Version = 1;
    public const string ApplicationSubprotocol = "viberails.http-relay.v1";
    public const string CredentialSubprotocolPrefix = "viberails.api-key.v1.";
    public const string RequestType = "http_request";
    public const string ResponseType = "http_response";
    public const string ErrorType = "http_error";
    public const string CancelType = "http_cancel";
    public const int MaxEnvelopeBytes = 512 * 1024;
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxHeaderBytes = 32 * 1024;
    public const int MaxTimeoutMs = 30_000;

    public static string CreateCredentialSubprotocol(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(apiKey))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return CredentialSubprotocolPrefix + encoded;
    }

    internal static Uri CreateWebSocketUri(string frontendUrl)
    {
        if (!Uri.TryCreate(frontendUrl, UriKind.Absolute, out var frontend)
            || !string.IsNullOrEmpty(frontend.UserInfo))
        {
            throw new HttpRelayConfigurationException("The VibeRails frontend URL is invalid.");
        }

        var scheme = frontend.Scheme.ToLowerInvariant() switch
        {
            "https" => "wss",
            "http" when frontend.IsLoopback => "ws",
            "http" => throw new HttpRelayConfigurationException(
                "The HTTP relay requires HTTPS except for a loopback development server."),
            _ => throw new HttpRelayConfigurationException(
                "The VibeRails frontend URL must use HTTPS.")
        };

        var builder = new UriBuilder(frontend)
        {
            Scheme = scheme,
            Path = frontend.AbsolutePath.TrimEnd('/') + "/ws/v1/http-relay",
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri;
    }

    internal static byte[] SerializeRequest(HttpRelayRequest request) =>
        EnsureEnvelopeLimit(JsonSerializer.SerializeToUtf8Bytes(
            request,
            HttpRelayJsonContext.Default.HttpRelayRequest));

    internal static byte[] SerializeCancel(string requestId) =>
        EnsureEnvelopeLimit(JsonSerializer.SerializeToUtf8Bytes(
            new HttpRelayCancel(Version, CancelType, requestId),
            HttpRelayJsonContext.Default.HttpRelayCancel));

    internal static object DeserializeInbound(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaxEnvelopeBytes)
            throw new HttpRelayProtocolException("The relay envelope size is invalid.");

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new HttpRelayProtocolException("The relay response has no message type.");
            }

            return typeElement.GetString() switch
            {
                ResponseType => JsonSerializer.Deserialize(
                    utf8Json,
                    HttpRelayJsonContext.Default.HttpRelayResponse)
                    ?? throw new HttpRelayProtocolException("The relay response is empty."),
                ErrorType => JsonSerializer.Deserialize(
                    utf8Json,
                    HttpRelayJsonContext.Default.HttpRelayError)
                    ?? throw new HttpRelayProtocolException("The relay error is empty."),
                _ => throw new HttpRelayProtocolException("The relay returned an unknown message type.")
            };
        }
        catch (JsonException ex)
        {
            throw new HttpRelayProtocolException("The relay returned invalid JSON.", ex);
        }
    }

    internal static void ValidateRequest(HttpRelayRequest request)
    {
        if (request.Version != Version
            || request.Type != RequestType
            || !Guid.TryParse(request.RequestId, out _)
            || request.TimeoutMs is <= 0 or > MaxTimeoutMs)
        {
            throw new HttpRelayProtocolException("The relay request envelope is invalid.");
        }

        if (request.Method is not ("GET" or "POST" or "PUT" or "DELETE"))
            throw new HttpRelayProtocolException("The relay request method is not supported.");

        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out _))
            throw new HttpRelayProtocolException("The relay request URI is invalid.");

        var headerBytes = request.Headers.Sum(static header =>
            Encoding.UTF8.GetByteCount(header.Key)
            + header.Value.Sum(static value => Encoding.UTF8.GetByteCount(value)));
        if (headerBytes > MaxHeaderBytes)
            throw new HttpRelayProtocolException("The relay request headers are too large.");

        if (request.Body is null)
            return;

        if (!string.Equals(request.Body.Encoding, "base64", StringComparison.Ordinal))
            throw new HttpRelayProtocolException("The relay request body encoding is not supported.");

        try
        {
            if (Convert.FromBase64String(request.Body.Data).Length > MaxBodyBytes)
                throw new HttpRelayProtocolException("The relay request body is too large.");
        }
        catch (FormatException ex)
        {
            throw new HttpRelayProtocolException("The relay request body is not valid base64.", ex);
        }
    }

    internal static byte[] DecodeBody(HttpRelayBody? body)
    {
        if (body is null)
            return [];
        if (!string.Equals(body.Encoding, "base64", StringComparison.Ordinal))
            throw new HttpRelayProtocolException("The relay response body encoding is not supported.");

        try
        {
            var bytes = Convert.FromBase64String(body.Data);
            if (bytes.Length > MaxBodyBytes)
                throw new HttpRelayProtocolException("The relay response body is too large.");
            return bytes;
        }
        catch (FormatException ex)
        {
            throw new HttpRelayProtocolException("The relay response body is not valid base64.", ex);
        }
    }

    private static byte[] EnsureEnvelopeLimit(byte[] envelope)
    {
        if (envelope.Length > MaxEnvelopeBytes)
            throw new HttpRelayProtocolException("The relay envelope is too large.");
        return envelope;
    }
}

public sealed record HttpRelayBody(string Encoding, string Data);

public sealed record HttpRelayRequest(
    int Version,
    string Type,
    string RequestId,
    string Method,
    string Uri,
    Dictionary<string, string[]> Headers,
    HttpRelayBody? Body,
    int TimeoutMs);

public sealed record HttpRelayResponse(
    int Version,
    string Type,
    string RequestId,
    int StatusCode,
    string? ReasonPhrase,
    Dictionary<string, string[]> Headers,
    HttpRelayBody? Body,
    long ElapsedMs);

public sealed record HttpRelayError(
    int Version,
    string Type,
    string RequestId,
    string ErrorCode,
    string Message,
    bool Retryable);

public sealed record HttpRelayCancel(
    int Version,
    string Type,
    string RequestId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HttpRelayRequest))]
[JsonSerializable(typeof(HttpRelayResponse))]
[JsonSerializable(typeof(HttpRelayError))]
[JsonSerializable(typeof(HttpRelayCancel))]
[JsonSerializable(typeof(HttpRelayBody))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal partial class HttpRelayJsonContext : JsonSerializerContext;

public class HttpRelayException : Exception
{
    public HttpRelayException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class HttpRelayConfigurationException : HttpRelayException
{
    public HttpRelayConfigurationException(string message) : base(message)
    {
    }
}

public sealed class HttpRelayProtocolException : HttpRelayException
{
    public HttpRelayProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class HttpRelayTransportException : HttpRelayException
{
    public HttpRelayTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class HttpRelayRemoteException : HttpRelayException
{
    public HttpRelayRemoteException(string errorCode, string message, bool retryable)
        : base(message)
    {
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    public string ErrorCode { get; }
    public bool Retryable { get; }
}
