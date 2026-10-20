namespace VibeRails.DTOs;

public sealed record SigningKeyInfo(Guid Id, string Name, string PublicKeyPem, string Fingerprint,
    DateTimeOffset CreatedUtc, Guid? CloudKeyId, DateTimeOffset? CloudSyncedUtc);
/// <summary><paramref name="Warnings"/> names files in the signing-keys folder that were skipped.</summary>
public sealed record SigningKeyListResponse(SigningKeyInfo[] Keys, bool ApiKeyConfigured, string[] Warnings);
public sealed record CreateSigningKeyRequest(string? Name, string? Password);
public sealed record SigningKeyPasswordRequest(string? Password);
public sealed record SignPayloadRequest(string? Password, string? PayloadBase64);
public sealed record SigningKeyMutationResponse(SigningKeyInfo Key, string SyncStatus, string? SyncError);
public sealed record SigningKeyExportResponse(string FileName, string EncryptedPrivateKeyPem, string PublicKeyPem);
/// <summary>Everything a verifier needs offline: the public key and fingerprint travel with the signature.</summary>
public sealed record SignedPayloadResponse(Guid? KeyId, string Fingerprint, string PublicKeyPem, string Algorithm,
    string PayloadBase64, string SignatureBase64);
public sealed record SigningKeyChallengeRequest(string PublicKeyPem);
public sealed record SigningKeyChallengeResponse(Guid ChallengeId, string PayloadBase64, DateTimeOffset ExpiresUtc);
public sealed record RegisterSigningKeyRequest(string Name, string PublicKeyPem, Guid ChallengeId, string ProofSignatureBase64);
public sealed record RegisteredSigningKeyResponse(Guid Id, string Fingerprint);
/// <summary>
/// Body of the public <c>POST /public/api/v1/signatures/verify</c> call made by Git Guard for commit
/// signatures. The server disallows unmapped members, so property names must stay in step with
/// VibeRails-Front's <c>VerifySignatureRequest</c>.
/// </summary>
public sealed record VerifySignatureRequest(Guid KeyId, string PayloadBase64, string SignatureBase64,
    string Algorithm, string Fingerprint);
/// <summary>The public verifier's answer; <paramref name="SignerEmail"/> is present only for a valid signature.</summary>
public sealed record PublicSignatureVerificationResponse(bool Valid, string? Reason, Guid KeyId, string? Fingerprint,
    string? SignerEmail, string? Algorithm, string? PayloadSha256, DateTime VerifiedUtc);
