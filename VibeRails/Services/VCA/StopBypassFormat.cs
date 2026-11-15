using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Launches;

namespace VibeRails.Services.VCA;

/// <summary>A STOP rule identified by its declaring file and exact text.</summary>
public sealed record StopBypassRule(string SourcePath, string RuleText);

/// <summary>The complete statement a person approves; no private key or machine path enters a commit.</summary>
public sealed record StopBypassScope(string RepositoryId, string? BaseCommit, string BaseTree,
    string StagedTree, string Reason, StopBypassRule[] Rules);

/// <summary>Signed evidence retained in the commit message.</summary>
public sealed record StopBypassApproval(StopBypassScope Scope, CommitSignature Signature);

/// <summary>Local request state. Only the separate human prompt may populate Approval.</summary>
public sealed record StopBypassRequest(string Id, StopBypassScope Scope, DateTimeOffset CreatedUtc,
    string Status = "pending", StopBypassApproval? Approval = null, string? Error = null);

/// <summary>Canonical signing statement and readable, tamper-evident commit trailers.</summary>
public static class StopBypassFormat
{
    public const string PayloadPrefix = "VibeRails STOP bypass v1\n";
    public const string Trailer = "VCA-STOP-Bypass:";
    public const string ReasonTrailer = "VCA-STOP-Reason:";
    public const string RuleTrailer = "VCA-STOP-Rule:";
    public const int MaxBytes = 64 * 1024;

    public static string RepositoryId(string root) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(LaunchService.ProjectKey(root))));

    public static void Validate(StopBypassScope scope)
    {
        if (!CommitSignatureFormat.IsFingerprint(scope.RepositoryId)
            || !CommitSignatureFormat.IsObjectId(scope.BaseTree) || !CommitSignatureFormat.IsObjectId(scope.StagedTree)
            || scope.BaseCommit is not null && !CommitSignatureFormat.IsObjectId(scope.BaseCommit)
            || string.IsNullOrWhiteSpace(scope.Reason) || scope.Reason.Length > 2000
            || scope.Reason != scope.Reason.Trim() || scope.Reason.Any(char.IsControl)
            || scope.Rules is not { Length: > 0 and <= 64 })
            throw new ArgumentException("A STOP bypass needs an exact staged snapshot, identified rules and a reason of 1–2,000 characters on one line.");
        foreach (var rule in scope.Rules)
            if (rule is null || string.IsNullOrWhiteSpace(rule.SourcePath) || string.IsNullOrWhiteSpace(rule.RuleText)
                || rule.SourcePath.Any(char.IsControl) || rule.RuleText.Any(char.IsControl)
                || rule.SourcePath.Length > 4096 || rule.RuleText.Length > 4096)
                throw new ArgumentException("The STOP bypass contains an invalid rule identity.");
        if (JsonSerializer.SerializeToUtf8Bytes(scope, StopBypassJson.Default.StopBypassScope).Length > MaxBytes / 2)
            throw new ArgumentException("The selected STOP rules are too large for one approval.");
    }

    public static byte[] BuildPayload(StopBypassScope scope)
    {
        Validate(scope);
        // The public verifier sees only a domain-separated digest, not rule text or the reason.
        var json = JsonSerializer.SerializeToUtf8Bytes(scope, StopBypassJson.Default.StopBypassScope);
        return Encoding.UTF8.GetBytes(PayloadPrefix + "scope " + Convert.ToHexStringLower(SHA256.HashData(json)) + "\n");
    }

    public static bool Matches(StopBypassScope scope, GitStagedSnapshot snapshot) =>
        snapshot.StagedIdentity is { } identity && scope.RepositoryId == RepositoryId(snapshot.RepositoryPath)
        && scope.BaseCommit == identity.BaseCommit && scope.BaseTree == identity.BaseTree && scope.StagedTree == identity.StagedTree;

    public static string Format(StopBypassApproval approval) => string.Join("\n",
        new[] { ReasonTrailer + " " + approval.Scope.Reason }
            .Concat(approval.Scope.Rules.Select(rule => RuleTrailer + " " + rule.SourcePath + " | " + rule.RuleText))
            .Append(Trailer + " v1 " + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(approval, StopBypassJson.Default.StopBypassApproval))));

    public static StopBypassApproval? Parse(string message)
    {
        var lines = BeforeScissors(message).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var entries = lines.Where(line => line.StartsWith(Trailer, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length > MaxBytes) return null;
        try
        {
            var value = entries[0][Trailer.Length..].Trim();
            if (!value.StartsWith("v1 ", StringComparison.Ordinal)) return null;
            var approval = JsonSerializer.Deserialize(Convert.FromBase64String(value[3..]), StopBypassJson.Default.StopBypassApproval);
            if (approval?.Scope is not { } scope || approval.Signature is not { } signature) return null;
            Validate(scope);
            if (signature.TreeId != scope.StagedTree || !CommitSignatureFormat.TryParseTrailer(CommitSignatureFormat.Format(signature), out _)) return null;
            // The human-readable audit text is mandatory and must match what was signed exactly.
            var expected = Format(approval).Split('\n');
            var actual = lines.Where(IsTrailer).ToArray();
            return expected.SequenceEqual(actual) ? approval : null;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException) { return null; }
    }

    public static bool IsTrailer(string line) => line.StartsWith(Trailer, StringComparison.OrdinalIgnoreCase)
        || line.StartsWith(ReasonTrailer, StringComparison.OrdinalIgnoreCase)
        || line.StartsWith(RuleTrailer, StringComparison.OrdinalIgnoreCase);

    // Conservatively treat the standard scissors marker as the end of the message,
    // regardless of core.commentChar. Evidence in a verbose diff must never approve a commit.
    internal static int FindScissorsLine(string[] lines) => Array.FindIndex(lines,
        line => line.TrimEnd('\r').EndsWith(" ------------------------ >8 ------------------------", StringComparison.Ordinal));

    internal static string BeforeScissors(string message)
    {
        var lines = message.Split('\n');
        var scissors = FindScissorsLine(lines);
        return scissors < 0 ? message : string.Join("\n", lines.Take(scissors));
    }
}

[JsonSerializable(typeof(StopBypassScope))]
[JsonSerializable(typeof(StopBypassApproval))]
[JsonSerializable(typeof(StopBypassRequest))]
internal partial class StopBypassJson : JsonSerializerContext;
