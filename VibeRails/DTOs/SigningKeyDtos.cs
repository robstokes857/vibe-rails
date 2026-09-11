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
