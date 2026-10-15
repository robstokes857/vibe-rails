using System.Net.WebSockets;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.PythonScripts;

/// <summary>
/// One signed script, one socket, one temporary PTY. The lifetime (no tab, recorder, observer,
/// replay or sharing; disconnect kills the tree) is <see cref="PrivatePtySession"/>'s.
/// </summary>
public static class PythonScriptTerminalSession
{
    private static readonly PrivatePtyText Text = new(
        "Invalid script terminal request.", "Could not start the script terminal.", "Script finished");

    /// <summary>Runs an authenticated dashboard connection until the script ends or its window closes.</summary>
    public static Task RunAsync(WebSocket socket, IPythonScriptService scripts, CancellationToken cancellationToken) =>
        // A PIN only lives in this bounded first-message preparation call, never in session state.
        PrivatePtySession.RunAsync(socket, (message, token) => PrepareAsync(message, scripts, token), Text, cancellationToken);

    private static async Task<PrivatePtyLaunch> PrepareAsync(
        ReadOnlyMemory<byte> message, IPythonScriptService scripts, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize(message.Span,
            AppJsonSerializerContext.Default.PythonScriptTerminalRequest);
        if (request is null) throw new ArgumentException("Invalid terminal dimensions.");
        PrivatePtySession.ValidateSize(request.Cols, request.Rows);
        try
        {
            var start = await scripts.PrepareTerminalRunAsync(request.Name, request.Pin, cancellationToken);
            return new PrivatePtyLaunch(start, request.Cols, request.Rows);
        }
        catch (PythonScriptValidationException error)
        {
            throw new PrivatePtyRejectedException(error.Message);
        }
    }
}
