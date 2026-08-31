using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeRails.Daemon.Ipc;

public enum DaemonControlCommand
{
    Ping,
    Status,
    Kick,
    Shutdown
}

public static class DaemonControlProtocol
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 4 * 1024;
    public const int MaximumResponseBytes = 64 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public const string ProtocolMismatchError = "protocol_mismatch";
    public const string InvalidRequestError = "invalid_request";
    public const string UnknownCommandError = "unknown_command";
    public const string HandlerError = "handler_error";

    public static string ToWireName(DaemonControlCommand command) => command switch
    {
        DaemonControlCommand.Ping => "PING",
        DaemonControlCommand.Status => "STATUS",
        DaemonControlCommand.Kick => "KICK",
        DaemonControlCommand.Shutdown => "SHUTDOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    public static bool TryParseCommand(string? value, out DaemonControlCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Equals("PING", StringComparison.OrdinalIgnoreCase))
            command = DaemonControlCommand.Ping;
        else if (value.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            command = DaemonControlCommand.Status;
        else if (value.Equals("KICK", StringComparison.OrdinalIgnoreCase))
            command = DaemonControlCommand.Kick;
        else if (value.Equals("SHUTDOWN", StringComparison.OrdinalIgnoreCase))
            command = DaemonControlCommand.Shutdown;
        else
            return false;

        return true;
    }
}

public sealed record DaemonControlRequest(int ProtocolVersion, string Command)
{
    public static DaemonControlRequest Create(DaemonControlCommand command) =>
        new(DaemonControlProtocol.Version, DaemonControlProtocol.ToWireName(command));
}

public sealed record DaemonControlResponse(
    int ProtocolVersion,
    bool Success,
    string? ErrorCode = null,
    string? Error = null,
    string? Message = null,
    JsonElement? Payload = null);

public sealed record DaemonControlHandlerResult(
    bool Success,
    string? Message = null,
    string? Error = null,
    JsonElement? Payload = null,
    Func<ValueTask>? AfterResponse = null)
{
    public static DaemonControlHandlerResult Ok(
        string? message = null,
        JsonElement? payload = null,
        Func<ValueTask>? afterResponse = null) =>
        new(true, message, Payload: payload, AfterResponse: afterResponse);

    public static DaemonControlHandlerResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// Application-provided command handler. The pipe layer owns validation, framing, timeouts, and
/// version negotiation; the consuming daemon owns command semantics.
/// </summary>
public interface IDaemonControlHandler
{
    ValueTask<DaemonControlHandlerResult> HandleAsync(
        DaemonControlCommand command,
        CancellationToken cancellationToken);
}

public enum DaemonControlClientOutcome
{
    Success,
    Rejected,
    ProtocolMismatch,
    Unreachable,
    InvalidResponse
}

public sealed record DaemonControlClientResult(
    DaemonControlClientOutcome Outcome,
    DaemonControlResponse? Response = null,
    string? Error = null)
{
    public bool IsReachable => Outcome is
        DaemonControlClientOutcome.Success or
        DaemonControlClientOutcome.Rejected or
        DaemonControlClientOutcome.ProtocolMismatch;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DaemonControlRequest))]
[JsonSerializable(typeof(DaemonControlResponse))]
internal sealed partial class DaemonControlJsonContext : JsonSerializerContext;
