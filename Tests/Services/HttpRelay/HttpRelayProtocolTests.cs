using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VibeRails.Services.HttpRelay;
using Xunit;

namespace Tests.Services.HttpRelay;

public sealed class HttpRelayProtocolTests
{
    [Fact]
    public void Request_UsesStableCamelCaseV1WireShape()
    {
        var request = new HttpRelayRequest(
            1,
            "http_request",
            "11111111-1111-1111-1111-111111111111",
            "POST",
            "https://jsonplaceholder.typicode.com/posts",
            new Dictionary<string, string[]> { ["content-type"] = ["application/json"] },
            new HttpRelayBody("base64", "e30="),
            30_000);

        var json = Encoding.UTF8.GetString(HttpRelayProtocol.SerializeRequest(request));

        Assert.Equal(
            """{"version":1,"type":"http_request","requestId":"11111111-1111-1111-1111-111111111111","method":"POST","uri":"https://jsonplaceholder.typicode.com/posts","headers":{"content-type":["application/json"]},"body":{"encoding":"base64","data":"e30="},"timeoutMs":30000}""",
            json);
    }

    [Fact]
    public void Credential_IsBase64UrlSubprotocol_WithoutPadding()
    {
        Assert.Equal(
            "viberails.api-key.v1.dGVzdC1rZXk",
            HttpRelayProtocol.CreateCredentialSubprotocol("test-key"));
    }

    [Theory]
    [InlineData("key with spaces / + = and punctuation !@#$%^&*()")]
    [InlineData("κλειδί-🔐-日本語")]
    public void CredentialSubprotocol_IsAlwaysAValidDotNetWebSocketToken(string apiKey)
    {
        using var socket = new ClientWebSocket();
        var credentialProtocol = HttpRelayProtocol.CreateCredentialSubprotocol(apiKey);

        // AddSubProtocol performs .NET's RFC token validation and throws for values that
        // would make Sec-WebSocket-Protocol invalid. The application token plus the
        // base64url credential token must both be safe for the real handshake path.
        socket.Options.AddSubProtocol(HttpRelayProtocol.ApplicationSubprotocol);
        socket.Options.AddSubProtocol(credentialProtocol);

        Assert.StartsWith(HttpRelayProtocol.CredentialSubprotocolPrefix, credentialProtocol);
        Assert.DoesNotContain('=', credentialProtocol);
    }

    [Fact]
    public void WebSocketUri_RequiresTlsExceptForLoopback()
    {
        Assert.Equal(
            "wss://viberails.ai/ws/v1/http-relay",
            HttpRelayProtocol.CreateWebSocketUri("https://viberails.ai").AbsoluteUri);
        Assert.Equal(
            "ws://127.0.0.1:5002/base/ws/v1/http-relay",
            HttpRelayProtocol.CreateWebSocketUri("http://127.0.0.1:5002/base/").AbsoluteUri);
        Assert.Throws<HttpRelayConfigurationException>(() =>
            HttpRelayProtocol.CreateWebSocketUri("http://example.com"));
    }

    [Fact]
    public void ResponseAndError_AreDiscriminatedByType()
    {
        var response = Assert.IsType<HttpRelayResponse>(HttpRelayProtocol.DeserializeInbound(
            """{"version":1,"type":"http_response","requestId":"11111111-1111-1111-1111-111111111111","statusCode":200,"reasonPhrase":"OK","headers":{},"body":{"encoding":"base64","data":"e30="},"elapsedMs":12}"""u8));
        var error = Assert.IsType<HttpRelayError>(HttpRelayProtocol.DeserializeInbound(
            """{"version":1,"type":"http_error","requestId":"11111111-1111-1111-1111-111111111111","errorCode":"upstream_timeout","message":"Timed out.","retryable":true}"""u8));

        Assert.Equal("{}", Encoding.UTF8.GetString(HttpRelayProtocol.DecodeBody(response.Body)));
        Assert.Equal("upstream_timeout", error.ErrorCode);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void RequestValidation_RejectsOversizedDecodedBody()
    {
        var request = new HttpRelayRequest(
            1,
            "http_request",
            Guid.NewGuid().ToString("D"),
            "POST",
            "https://jsonplaceholder.typicode.com/posts",
            [],
            new HttpRelayBody(
                "base64",
                Convert.ToBase64String(new byte[HttpRelayProtocol.MaxBodyBytes + 1])),
            30_000);

        Assert.Throws<HttpRelayProtocolException>(() => HttpRelayProtocol.ValidateRequest(request));
    }
}
