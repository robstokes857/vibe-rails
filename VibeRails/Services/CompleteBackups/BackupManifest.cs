using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace VibeRails.Services.Backups;

/// <summary>An immutable archive: concatenating its ordered, independently verified parts yields PayloadFormat.</summary>
public sealed record BackupManifest(
    int SchemaVersion, string ComputerId, string ComputerName, string Dataset, string Version,
    DateTime SourceStartedUtc, DateTime SourceCompletedUtc, string SourceFingerprint,
    string PayloadFormat, long PayloadBytes, List<BackupPart> Parts, List<string> CoverageIssues);

/// <summary>SHA-256 covers exactly these bytes, before concatenation or decompression.</summary>
public sealed record BackupPart(string Sha256, int Bytes);

/// <summary>The checksum covers the exact UTF-8 manifest; every referenced part was independently hashed by the server.</summary>
public sealed record BackupReceipt(int AccountId, string ComputerId, string Dataset, string Version,
    string ManifestSha256, long PayloadBytes, DateTime SourceStartedUtc, DateTime SourceCompletedUtc,
    DateTime ReceivedUtc, bool ChecksumsVerified);

/// <summary>Account identity comes from the authenticated upload server, never from a request owner field.</summary>
public sealed record BackupAccount(int AccountId);

/// <summary>Bounded resume response for a single content-addressed part.</summary>
public sealed record BackupPartReceipt(string Sha256, int Bytes, bool ChecksumsVerified);

/// <summary>Wire limits shared with the backup ingest contract in VibeRails-Front.</summary>
public static class BackupFormat
{
    public const int PartBytes = 4 * 1024 * 1024;
    public const int MaxParts = 50_000;
    public const int MaxManifestBytes = 8 * 1024 * 1024;
    public static readonly string[] Datasets = ["board", "configuration", "state", "proxy"];
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsValid(BackupManifest m) => m.SchemaVersion == 1
        && Guid.TryParseExact(m.ComputerId, "N", out var computer) && computer != Guid.Empty
        && Guid.TryParseExact(m.Version, "N", out var version) && version != Guid.Empty
        && m.ComputerName is { Length: > 0 and <= 80 } && !m.ComputerName.Any(char.IsControl)
        && Datasets.Contains(m.Dataset) && IsHash(m.SourceFingerprint)
        && m.SourceStartedUtc.Kind == DateTimeKind.Utc && m.SourceCompletedUtc.Kind == DateTimeKind.Utc
        && m.SourceStartedUtc <= m.SourceCompletedUtc && m.SourceStartedUtc.Year >= 2020
        && m.PayloadFormat == (m.Dataset == "configuration" ? "zip" : "sqlite.br")
        && m.Parts is { Count: > 0 and <= MaxParts }
        && m.Parts.All(p => p is not null && IsHash(p.Sha256) && p.Bytes is > 0 and <= PartBytes)
        && m.Parts.Sum(p => (long)p.Bytes) == m.PayloadBytes
        && m.CoverageIssues is { Count: <= 1000 } && m.CoverageIssues.All(s => s is { Length: <= 1000 });
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupReceipt))]
[JsonSerializable(typeof(BackupAccount))]
[JsonSerializable(typeof(BackupPartReceipt))]
[JsonSerializable(typeof(BackupCheckpoint))]
[JsonSerializable(typeof(BackupCoverage))]
internal partial class BackupJson : JsonSerializerContext;

/// <summary>Durable scheduling and receipt state. Pending manifests and parts are stored separately.</summary>
public sealed record BackupCheckpoint
{
    public string Status { get; init; } = "pending";
    public string? Error { get; init; }
    public string? PendingVersion { get; init; }
    public int NextPart { get; init; }
    public int Attempts { get; init; }
    public DateTime NextAttemptUtc { get; init; }
    public DateTime? LastCheckedUtc { get; init; }
    public string? SourceFingerprint { get; init; }
    public BackupReceipt? Receipt { get; init; }
    public List<string> CoverageIssues { get; init; } = [];
}

/// <summary>Local coverage never infers completeness from session or Board sync receipts.</summary>
public sealed record BackupDatasetCoverage(string Dataset, BackupCheckpoint State);
public sealed record BackupCoverage(bool Configured, List<BackupDatasetCoverage> Datasets, List<string> Exclusions);
