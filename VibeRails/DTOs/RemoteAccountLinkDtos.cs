namespace VibeRails.DTOs;

/// <summary>Display-only account details returned by the hosted approval.</summary>
public sealed record RemoteAccountIdentity(string? Email = null, string? Name = null);

/// <summary>Local sign-in progress. Neither credential is exposed to the browser.</summary>
public sealed record RemoteAccountLinkStatus(
    string Status, string? UserCode = null, string? VerificationUri = null,
    DateTimeOffset? ExpiresAt = null, int Interval = 3, string? KeyHint = null,
    RemoteAccountIdentity? Account = null, string? Error = null);

internal sealed record DeviceLinkRequest(string ComputerName, string ClientVersion);
internal sealed record DeviceLinkSecret(string DeviceCode);
internal sealed record DeviceLinkCreated(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);
internal sealed record DeviceLinkToken(string ApiKey, string? KeyPrefix, string? KeySuffix, RemoteAccountIdentity? Account);
