using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Messaging;

namespace VibeRails.Routes;

public static class ProxyRoutes
{
    public static void Map(WebApplication app)
    {
        // POST /api/v1/proxy/verify — verify a signed message
        app.MapPost("/api/v1/proxy/verify", (SignedMessage msg, MessageSignatureValidator validator) =>
        {
            var verified = validator.VerifyMessage(msg.Message, msg.Signature);
            return Results.Ok(new SignatureVerificationResponse(verified, verified ? "Signature valid" : "Signature invalid"));
        }).WithName("VerifySignedMessage");

        // GET /api/v1/proxy/ws — WebSocket proxy to VibeRails-Front
        // Query params: url (frontend base URL), apiKey
        // Browser connects here; server connects upstream and relays messages with verification status
        app.Map("/api/v1/proxy/ws", async (HttpContext context, MessageSignatureValidator validator) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            var frontendUrl = context.Request.Query["url"].ToString();
            var apiKey = context.Request.Query["apiKey"].ToString();

            if (string.IsNullOrEmpty(frontendUrl))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing 'url' query parameter");
                return;
            }

            // Accept browser WebSocket
            using var browserSocket = await context.WebSockets.AcceptWebSocketAsync();

            // Connect upstream to VibeRails-Front
            using var upstreamClient = new ClientWebSocket();
            upstreamClient.Options.SetRequestHeader("X-Api-Key", apiKey);

            var wsUri = frontendUrl.Replace("https://", "wss://").Replace("http://", "ws://");
            if (!wsUri.StartsWith("ws://") && !wsUri.StartsWith("wss://"))
                wsUri = "ws://" + wsUri;
            if (!wsUri.Contains("/ws/v1/terminal"))
                wsUri = wsUri.TrimEnd('/') + "/ws/v1/terminal";

            try
            {
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await upstreamClient.ConnectAsync(new Uri(wsUri), connectCts.Token);
            }
            catch (Exception ex)
            {
                var errorRelay = new ProxyRelayMessage("error", $"Failed to connect upstream: {ex.Message}");
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorRelay, AppJsonSerializerContext.Default.ProxyRelayMessage));
                await browserSocket.SendAsync(errorBytes, WebSocketMessageType.Text, true, CancellationToken.None);
                await browserSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Upstream connection failed", CancellationToken.None);
                return;
            }

            var ct = context.RequestAborted;

            // Relay upstream → browser (with signature verification)
            var upstreamToBrowser = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (upstreamClient.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await upstreamClient.ReceiveAsync(buffer, ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string rawMessage;
                            if (result.EndOfMessage)
                            {
                                rawMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            }
                            else
                            {
                                var sb = new StringBuilder(Encoding.UTF8.GetString(buffer, 0, result.Count));
                                while (!result.EndOfMessage)
                                {
                                    result = await upstreamClient.ReceiveAsync(buffer, ct);
                                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                                }
                                rawMessage = sb.ToString();
                            }

                            // Try to parse as signed message and verify
                            string relay;
                            try
                            {
                                var signed = JsonSerializer.Deserialize(rawMessage, AppJsonSerializerContext.Default.SignedMessage);
                                if (signed != null && !string.IsNullOrEmpty(signed.Signature))
                                {
                                    var verified = validator.VerifyMessage(signed.Message, signed.Signature);
                                    var relayMsg = new ProxyRelayMessage("signed", signed.Message, signed.Signature, verified);
                                    relay = JsonSerializer.Serialize(relayMsg, AppJsonSerializerContext.Default.ProxyRelayMessage);
                                }
                                else
                                {
                                    var relayMsg = new ProxyRelayMessage("plain", rawMessage);
                                    relay = JsonSerializer.Serialize(relayMsg, AppJsonSerializerContext.Default.ProxyRelayMessage);
                                }
                            }
                            catch
                            {
                                // Not JSON or not a signed message — pass through as plain
                                var relayMsg = new ProxyRelayMessage("plain", rawMessage);
                                relay = JsonSerializer.Serialize(relayMsg, AppJsonSerializerContext.Default.ProxyRelayMessage);
                            }

                            var relayBytes = Encoding.UTF8.GetBytes(relay);
                            if (browserSocket.State == WebSocketState.Open)
                                await browserSocket.SendAsync(relayBytes, WebSocketMessageType.Text, true, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }, ct);

            // Relay browser → upstream
            var browserToUpstream = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (browserSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await browserSocket.ReceiveAsync(buffer, ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        if (result.MessageType == WebSocketMessageType.Text && upstreamClient.State == WebSocketState.Open)
                        {
                            await upstreamClient.SendAsync(
                                new ArraySegment<byte>(buffer, 0, result.Count),
                                WebSocketMessageType.Text,
                                result.EndOfMessage,
                                ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }, ct);

            await Task.WhenAny(upstreamToBrowser, browserToUpstream);

            // Clean up both sides
            if (upstreamClient.State == WebSocketState.Open)
            {
                try { await upstreamClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
                catch { }
            }
            if (browserSocket.State == WebSocketState.Open)
            {
                try { await browserSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
                catch { }
            }
        });
    }
}
