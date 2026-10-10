using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeRails.Services.Board.Sharing;

// This is a data-only wire contract, deliberately independent of desktop persistence entities.
public sealed record CardShareSnapshot(int Schema, string SourceKey, string DisplayId, string Title,
    string Description, string Board, string Lane, string Type, string Priority, string? Assignee,
    int? Points, IReadOnlyList<string> Tags, bool Blocked, bool Flagged,
    DateTime CreatedUtc, DateTime UpdatedUtc, IReadOnlyList<CardShareEntry> Discussion,
    IReadOnlyList<CardShareEntry> History, IReadOnlyList<CardShareSession> Sessions,
    IReadOnlyList<CardShareCommit> Commits, IReadOnlyList<CardShareAttachment> Attachments,
    IReadOnlyList<CardShareRelated> RelatedCards, IReadOnlyList<CardShareEvidence> Evidence);
public sealed record CardShareEntry(string Author, string? Purpose, string Body, DateTime CreatedUtc);
public sealed record CardShareSession(Guid SourceId, string Name, string Cli, string Origin,
    DateTime CreatedUtc, DateTime? EndedUtc, int? ExitCode, string? Summary);
public sealed record CardShareCommit(string Sha, string Author, string Message, DateTime CommittedUtc,
    IReadOnlyList<CardShareFile> Files, string? UnavailableReason);
public sealed record CardShareFile(string Path, string Before, string After);
public sealed record CardShareAttachment(string Name, string MimeType, long Bytes, DateTime CreatedUtc, string ContentBase64);
public sealed record CardShareRelated(string Key, string DisplayId, string Title);
public sealed record CardShareEvidence(string Kind, string Title, string Status, DateTime CreatedUtc, string Body);
public sealed record CardShareLinkDto(int Id, string SourceKey, string DisplayName, string SharePath,
    DateTime CreatedUtc, DateTime ExpiresUtc, string Status, DateTime UpdatedUtc, string? LocalCardId = null);
public sealed record CardShareSource(int Id, string SourceKey, string Revision, string? LocalCardId = null);
public sealed record CardSharePublishRequest(string DisplayName, CardShareSnapshot Snapshot, string? LocalCardId = null);
public sealed record CardShareRefreshRequest(string Revision, CardShareSnapshot Snapshot);
public sealed record CardShareNameRequest(string DisplayName);

public sealed record CardSharePublished(CardShareLinkDto Link, IReadOnlyList<Guid> UploadSessions);
public sealed record CardShareRefreshResult(bool Updated, IReadOnlyList<Guid> UploadSessions);
public sealed record CardShareResult(bool Success, string Message, CardShareLinkDto? Link = null,
    IReadOnlyList<CardShareLinkDto>? Links = null, int? NextBefore = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 32)]
[JsonSerializable(typeof(CardShareSnapshot))]
[JsonSerializable(typeof(CardSharePublishRequest))]
[JsonSerializable(typeof(CardShareRefreshRequest))]
[JsonSerializable(typeof(CardShareNameRequest))]
[JsonSerializable(typeof(CardSharePublished))]
[JsonSerializable(typeof(CardShareRefreshResult))]
[JsonSerializable(typeof(List<CardShareSource>))]
[JsonSerializable(typeof(List<CardShareLinkDto>))]
[JsonSerializable(typeof(CardShareResult))]
internal partial class CardSharingJsonContext : JsonSerializerContext;
