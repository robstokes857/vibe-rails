using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

public static class AgentToolRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/agent-tools/terminal", async (
            IAgentTerminalToolService tools,
            CancellationToken cancellationToken) =>
        {
            var response = await tools.ListTerminalsAsync(cancellationToken);
            return Results.Ok(response);
        }).WithName("AgentToolListTerminals");

        app.MapPost("/api/v1/agent-tools/terminal/open", async (
            IAgentTerminalToolService tools,
            AgentToolOpenTerminalRequest? request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await tools.OpenTerminalAsync(
                    request ?? new AgentToolOpenTerminalRequest(),
                    cancellationToken);
                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("AgentToolOpenTerminal");

        app.MapPost("/api/v1/agent-tools/terminal/input", async (
            IAgentTerminalToolService tools,
            AgentToolSendInputRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new ErrorResponse("Input request is required."));
            }

            try
            {
                var response = await tools.SendInputAsync(
                    request.TabId,
                    new TerminalInputRequest(request.Text, request.Submit),
                    cancellationToken);

                return response.Success
                    ? Results.Ok(response)
                    : Results.BadRequest(new ErrorResponse(response.Message));
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("AgentToolSendTerminalInput");

        app.MapPost("/api/v1/agent-tools/terminal/{tabId}/input", async (
            string tabId,
            IAgentTerminalToolService tools,
            TerminalInputRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new ErrorResponse("Input request is required."));
            }

            try
            {
                var response = await tools.SendInputAsync(tabId, request, cancellationToken);
                return response.Success
                    ? Results.Ok(response)
                    : Results.BadRequest(new ErrorResponse(response.Message));
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("AgentToolSendTerminalInputByTab");

        app.MapPost("/api/v1/agent-tools/terminal/snapshot", async (
            IAgentTerminalToolService tools,
            AgentToolSnapshotRequest? request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await tools.CaptureSnapshotAsync(request?.TabId, cancellationToken);
                return response == null
                    ? Results.NotFound(new ErrorResponse("No active terminal session was found for that tab."))
                    : Results.Ok(response);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("AgentToolGetTerminalSnapshot");

        app.MapGet("/api/v1/agent-tools/terminal/{tabId}/snapshot", async (
            string tabId,
            IAgentTerminalToolService tools,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await tools.CaptureSnapshotAsync(tabId, cancellationToken);
                return response == null
                    ? Results.NotFound(new ErrorResponse("No active terminal session was found for that tab."))
                    : Results.Ok(response);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("AgentToolGetTerminalSnapshotByTab");

        app.Map("/api/v1/agent-tools/ws", async (
            HttpContext context,
            IAgentTerminalToolService tools) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);
            await HandleControlWebSocketAsync(webSocket, tools, context.RequestAborted);
        });
    }

    private static async Task HandleControlWebSocketAsync(
        WebSocket webSocket,
        IAgentTerminalToolService tools,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            string? message;
            try
            {
                message = await ReceiveTextMessageAsync(webSocket, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                await SendResponseAsync(webSocket, new AgentToolControlResponse(null, false, ex.Message), cancellationToken);
                break;
            }

            if (message == null)
            {
                break;
            }

            AgentToolControlResponse response;
            try
            {
                var request = JsonSerializer.Deserialize(
                    message,
                    AppJsonSerializerContext.Default.AgentToolControlRequest);

                if (request == null || string.IsNullOrWhiteSpace(request.Action))
                {
                    response = new AgentToolControlResponse(request?.Id, false, "Action is required.");
                }
                else
                {
                    response = await DispatchAsync(request, tools, cancellationToken);
                }
            }
            catch (JsonException ex)
            {
                response = new AgentToolControlResponse(null, false, $"Invalid JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                response = new AgentToolControlResponse(null, false, ex.Message);
            }

            await SendResponseAsync(webSocket, response, cancellationToken);
        }

        if (webSocket.State == WebSocketState.Open)
        {
            try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
            catch { }
        }
    }

    private static async Task<AgentToolControlResponse> DispatchAsync(
        AgentToolControlRequest request,
        IAgentTerminalToolService tools,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Action.Trim().ToLowerInvariant() switch
            {
                "list_terminals" => await ListAsync(request.Id, tools, cancellationToken),
                "open_terminal" => await OpenAsync(request, tools, cancellationToken),
                "send_terminal_input" => await SendInputAsync(request, tools, cancellationToken),
                "get_terminal_snapshot" => await SnapshotAsync(request, tools, cancellationToken),
                _ => new AgentToolControlResponse(request.Id, false, $"Unknown action: {request.Action}")
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return new AgentToolControlResponse(request.Id, false, ex.Message);
        }
    }

    private static async Task<AgentToolControlResponse> ListAsync(
        string? id,
        IAgentTerminalToolService tools,
        CancellationToken cancellationToken)
    {
        var data = await tools.ListTerminalsAsync(cancellationToken);
        return new AgentToolControlResponse(
            id,
            true,
            Data: ToJsonElement(data, AppJsonSerializerContext.Default.AgentToolTerminalListResponse));
    }

    private static async Task<AgentToolControlResponse> OpenAsync(
        AgentToolControlRequest request,
        IAgentTerminalToolService tools,
        CancellationToken cancellationToken)
    {
        var payload = DeserializePayload(
            request.Payload,
            AppJsonSerializerContext.Default.AgentToolOpenTerminalRequest)
            ?? new AgentToolOpenTerminalRequest();

        var data = await tools.OpenTerminalAsync(payload, cancellationToken);
        return new AgentToolControlResponse(
            request.Id,
            true,
            Data: ToJsonElement(data, AppJsonSerializerContext.Default.TerminalTabStatusResponse));
    }

    private static async Task<AgentToolControlResponse> SendInputAsync(
        AgentToolControlRequest request,
        IAgentTerminalToolService tools,
        CancellationToken cancellationToken)
    {
        var payload = DeserializePayload(
            request.Payload,
            AppJsonSerializerContext.Default.AgentToolSendInputRequest);

        if (payload == null)
        {
            return new AgentToolControlResponse(request.Id, false, "Payload is required.");
        }

        var data = await tools.SendInputAsync(
            payload.TabId,
            new TerminalInputRequest(payload.Text, payload.Submit),
            cancellationToken);

        return data.Success
            ? new AgentToolControlResponse(
                request.Id,
                true,
                Data: ToJsonElement(data, AppJsonSerializerContext.Default.TerminalInputResponse))
            : new AgentToolControlResponse(request.Id, false, data.Message);
    }

    private static async Task<AgentToolControlResponse> SnapshotAsync(
        AgentToolControlRequest request,
        IAgentTerminalToolService tools,
        CancellationToken cancellationToken)
    {
        var payload = DeserializePayload(
            request.Payload,
            AppJsonSerializerContext.Default.AgentToolSnapshotRequest);

        var data = await tools.CaptureSnapshotAsync(payload?.TabId, cancellationToken);
        return data == null
            ? new AgentToolControlResponse(request.Id, false, "No active terminal session was found for that tab.")
            : new AgentToolControlResponse(
                request.Id,
                true,
                Data: ToJsonElement(data, AppJsonSerializerContext.Default.TerminalSnapshotResponse));
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("Only text JSON messages are supported.");
            }

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > TerminalControlProtocol.MaxMessageBytes)
            {
                throw new InvalidOperationException($"Message exceeds {TerminalControlProtocol.MaxMessageBytes} bytes.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }

    private static async Task SendResponseAsync(
        WebSocket webSocket,
        AgentToolControlResponse response,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            response,
            AppJsonSerializerContext.Default.AgentToolControlResponse);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static T? DeserializePayload<T>(JsonElement? payload, JsonTypeInfo<T> typeInfo)
    {
        if (payload == null || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return JsonSerializer.Deserialize(payload.Value.GetRawText(), typeInfo);
    }

    private static JsonElement ToJsonElement<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);
}
