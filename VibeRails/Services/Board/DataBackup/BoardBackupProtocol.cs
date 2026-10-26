using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace VibeRails.Services.Board.Backup;

public sealed record BoardBackupManifest(int Schema, string ComputerId, string ComputerName, string Version,
    DateTime StartedUtc, DateTime CompletedUtc, List<string> Records, List<string> Contents);
public sealed record BoardBackupReceipt(int AccountId, string ComputerId, string Version, string ManifestSha256,
    int Records, int Contents, DateTime ReceivedUtc);
public sealed record BoardBackupAccount(int AccountId);
public sealed record BoardBackupContentReceipt(string Sha256, int Bytes);
public sealed record BoardBackupRowsReceipt(List<string> Hashes);
public sealed record BoardBackupInventoryRequest(string Kind, List<string> Hashes);
public sealed record BoardBackupInventory(List<string> Missing);
public sealed record BoardBackupState
{
    public string? PendingVersion { get; init; }
    public int NextContent { get; init; }
    public int NextRecord { get; init; }
    public DateTime NextAttemptUtc { get; init; }
    public int Attempts { get; init; }
    public string? Fingerprint { get; init; }
    public BoardBackupReceipt? Receipt { get; init; }
    public string? Error { get; init; }
}

public static class BoardBackupProtocol
{
    public const int ContentBytes = 2 * 1024 * 1024;
    public const int RequestBytes = 4 * 1024 * 1024;
    public const int ManifestBytes = 16 * 1024 * 1024;
    public const int BatchRows = 256;
    public const int MaxItems = 100_000;
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string NewComputerId() => "bkc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(14));
    public static string NewVersion() => "bks_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(14));
    public static bool IsComputerId(string? value) => IsId(value, "bkc_");
    public static bool IsVersion(string? value) => IsId(value, "bks_");
    private static bool IsId(string? value, string prefix) => value is { Length: 32 } && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;
    public static bool IsValid(BoardBackupManifest m) => m.Schema == 1 && IsComputerId(m.ComputerId) && IsVersion(m.Version)
        && m.ComputerName is { Length: > 0 and <= 80 } && !m.ComputerName.Any(char.IsControl)
        && m.StartedUtc.Kind == DateTimeKind.Utc && m.CompletedUtc.Kind == DateTimeKind.Utc
        && m.StartedUtc.Year >= 2020 && m.StartedUtc <= m.CompletedUtc
        && ValidHashes(m.Records) && ValidHashes(m.Contents);
    private static bool ValidHashes(List<string>? values) => values is { Count: <= MaxItems }
        && values.All(IsHash) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BoardBackupManifest))]
[JsonSerializable(typeof(BoardBackupReceipt))]
[JsonSerializable(typeof(BoardBackupAccount))]
[JsonSerializable(typeof(BoardBackupContentReceipt))]
[JsonSerializable(typeof(BoardBackupRowsReceipt))]
[JsonSerializable(typeof(BoardBackupInventoryRequest))]
[JsonSerializable(typeof(BoardBackupInventory))]
[JsonSerializable(typeof(BoardBackupState))]
internal partial class BoardBackupJson : JsonSerializerContext;
