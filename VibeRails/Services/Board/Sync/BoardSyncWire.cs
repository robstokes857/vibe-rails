using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sync;

/// <summary>
/// The desktop side of the viberails.ai Board sync wire (VB-51): what <c>POST api/v1/boards/publish</c>,
/// <c>POST api/v1/boards/{id}/push</c> and <c>GET api/v1/boards/{id}/entries</c> exchange. Shapes
/// mirror <c>VibeRails-Front/Services/Boards/BoardSyncContract.cs</c>; both sides validate incoming
/// values before applying them.
/// </summary>
public static class BoardSyncWire
{
    public const string KindCreated = "created";
    public const string KindChange = "change";
    public const string KindDeleted = "deleted";
    public const string KindRestored = "restored";
    public const string KindComment = "comment";
    public const string KindNote = "note";

    public const string FieldTitle = "title";
    public const string FieldDescription = "description";
    public const string FieldType = "type";
    public const string FieldPriority = "priority";
    public const string FieldPoints = "points";
    public const string FieldAssignee = "assignee";
    public const string FieldTags = "tags";
    public const string FieldBlocked = "blocked";
    public const string FieldFlagged = "flagged";
    public const string FieldLane = "lane";

    // The bounded machine-readable codes the desktop recognises in an error body; anything else is
    // treated as an ordinary retryable HTTP failure and its prose is never read.
    public const string CodeInvalidEntry = "invalid_entry";
    public const string CodeInvalidRequest = "invalid_request";
    public const string CodeBoardNotFound = "board_not_found";
    public const string CodeWriteConflict = "write_conflict";

    // Keep incoming bounds aligned with the hosted BoardSyncContract / BoardSyncLimits.
    public const int MaxIdLength = 64;
    public const int MaxOtherBodyLength = 2_000;
    public const int MaxAuthorLabelLength = 200;
    public const int MaxAuthorCliLength = 64;
    public const int MaxChangesBytes = 1024 * 1024;

    /// <summary>
    /// An id as the hosted wire allows it (entry, lane): 1–64 ASCII letters, digits, <c>_</c> or <c>-</c>,
    /// starting with a letter or digit. A pulled comment or note keeps its server id in this shape.
    /// </summary>
    public static bool IsOpaqueId(string? value) => value is { Length: > 0 and <= MaxIdLength }
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>The <c>"to"</c> value of one field in a <c>changes</c> object, or null when the field is absent.</summary>
    public static JsonElement? FieldTo(JsonElement? changes, string field)
    {
        if (changes is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(field, out var change)
            || change.ValueKind != JsonValueKind.Object
            || !change.TryGetProperty("to", out var to))
            return null;
        return to;
    }
}

public sealed record BoardSyncLaneWire(string Id, string Name, string? Color, int Position);

public sealed record BoardSyncAuthorWire(string Kind, string Label, string? Cli);

/// <summary>A Card Log entry as pushed. <see cref="Changes"/> is the row's JSON verbatim (created / change only).</summary>
public sealed record BoardSyncEntryWire(
    string Id,
    string CardId,
    string CardKey,
    string Kind,
    BoardSyncAuthorWire Author,
    string Body,
    DateTime CreatedUtc,
    JsonElement? Changes);

public sealed record BoardSyncPublishRequest(string LocalBoardId, string Name, string KeyPrefix, List<BoardSyncLaneWire> Lanes, string? DisplayPrefix = null);

public sealed record BoardSyncPublishResponse(Guid RemoteBoardId, string Name, long LastSeq);

/// <summary>Null <see cref="Name"/>, <see cref="KeyPrefix"/> and <see cref="Lanes"/> mean unchanged.</summary>
public sealed record BoardSyncPushRequest(string? Name, string? KeyPrefix, List<BoardSyncLaneWire>? Lanes, List<BoardSyncEntryWire> Entries, string? DisplayPrefix = null);

public sealed record BoardSyncAcceptedWire(string Id, long Seq);

public sealed record BoardSyncPushResponse(List<BoardSyncAcceptedWire> Accepted, long LastSeq);

/// <summary>A Card Log entry as pulled: the pushed shape plus the server sequence and origin (desktop / web).</summary>
public sealed record BoardSyncPulledEntryWire(
    string Id,
    string CardId,
    string CardKey,
    string Kind,
    BoardSyncAuthorWire Author,
    string Body,
    JsonElement? Changes,
    DateTime CreatedUtc,
    long Seq,
    string Origin);

public sealed record BoardSyncPullResponse(List<BoardSyncPulledEntryWire> Entries, long LastSeq, bool HasMore);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BoardSyncPublishRequest))]
[JsonSerializable(typeof(BoardSyncPublishResponse))]
[JsonSerializable(typeof(BoardSyncPushRequest))]
[JsonSerializable(typeof(BoardSyncPushResponse))]
[JsonSerializable(typeof(BoardSyncPullResponse))]
[JsonSerializable(typeof(BoardSyncActivityWire))]
[JsonSerializable(typeof(BoardSyncActivityAck))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(BoardRemoteDescriptor))]
[JsonSerializable(typeof(List<BoardRemoteDescriptor>))]
[JsonSerializable(typeof(BoardSharingOverview))]
[JsonSerializable(typeof(BoardSharingEmailRequest))]
[JsonSerializable(typeof(BoardSharingResult))]
public sealed partial class BoardSyncJsonContext : JsonSerializerContext;

/// <summary>
/// A failed exchange with viberails.ai. <see cref="Code"/> is one of the server's recognised codes
/// (<c>invalid_entry</c>, <c>invalid_request</c>, <c>board_not_found</c>, <c>write_conflict</c>) or one
/// of the desktop's own: <c>no_api_key</c>, <c>no_endpoint</c>, <c>destination_changed</c>,
/// <c>unauthorized</c>, <c>network</c>, <c>response_too_large</c>, <c>invalid_response</c>,
/// <c>http_NNN</c>. <see cref="EntryId"/> names the rejected entry of an <c>invalid_entry</c>.
/// </summary>
public sealed class BoardSyncClientException(string message, string code, int status = 0, string? entryId = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string? EntryId { get; } = entryId;
}

/// <summary>
/// Where the desktop talks to: <c>VibeRails:BoardSync:EndpointUrl</c>, else <c>{FrontendUrl}/api/v1/boards</c>.
/// HTTPS is mandatory because the API key rides in a header; the one exception is a loopback host,
/// so a local VibeRails-Front can be exercised end to end without a certificate. Anything else
/// resolves to null and the sync stays off.
/// </summary>
public static class BoardSyncEndpoint
{
    public const string ConfigurationKey = "VibeRails:BoardSync:EndpointUrl";
    public const string DefaultPath = "/api/v1/boards";

    public static Uri? Resolve(string? configuredEndpoint, string? frontendUrl)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredEndpoint)
            ? string.IsNullOrWhiteSpace(frontendUrl) ? null : frontendUrl.TrimEnd('/') + DefaultPath
            : configuredEndpoint;
        if (candidate is null || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return uri;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback
            ? uri
            : null;
    }

    /// <summary>The page on viberails.ai that shows a published board, for the status view.</summary>
    public static string? BoardPage(string? frontendUrl, string? remoteBoardId) =>
        string.IsNullOrWhiteSpace(frontendUrl) || string.IsNullOrWhiteSpace(remoteBoardId)
            ? null
            : frontendUrl.TrimEnd('/') + "/Boards/" + Uri.EscapeDataString(remoteBoardId);

    public static Uri? FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        Resolve(configuration[ConfigurationKey], configuration["VibeRails:FrontendUrl"] ?? ParserConfigs.GetFrontendUrl());
}
