using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.VCA;

public enum CommitSignatureStatus
{
    /// <summary>The registered key signed this tree and its account email is verified.</summary>
    Valid,

    /// <summary>A definitive no: unknown or revoked key, wrong signature, unverified email.</summary>
    Rejected,

    /// <summary>No answer: offline, timed out, rate limited, or the service failed.</summary>
    Unavailable
}

/// <param name="Detail">A sentence fragment for the hook transcript, never a response body.</param>
/// <param name="SignerEmail">The verified account email, only when <see cref="Status"/> is Valid.</param>
public sealed record CommitSignatureVerification(
    CommitSignatureStatus Status,
    string Detail,
    string? SignerEmail = null);

/// <summary>Decides whether a commit signature is genuine and who made it.</summary>
public interface ICommitSignatureVerifier
{
    Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies commit signatures with the public endpoint
/// <c>POST https://viberails.ai/public/api/v1/signatures/verify</c>, which checks the signature
/// against the key registered on the server (never a key supplied by the caller) and discloses the
/// owning account's email only when the signature is valid and that email is verified.
/// </summary>
/// <remarks>
/// Fixed HTTPS host, no redirects, no cookies, a 15-second deadline and a 16-KiB response bound,
/// matching the signing-key registration client. Only the key id, fingerprint, payload and signature
/// are sent; the payload is the domain-separated tree statement, never repository content.
/// Definitive answers are cached for the life of the process, because commit-msg re-validates once
/// more after the popup appends a signature; network failures are never cached.
/// </remarks>
public sealed class ViberailsCommitSignatureVerifier : ICommitSignatureVerifier, IDisposable
{
    public const string VerifyUrl = "https://viberails.ai/public/api/v1/signatures/verify";
    private const int MaxResponseBytes = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly Lazy<ViberailsCommitSignatureVerifier> SharedInstance = new(() => new());

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private readonly ConcurrentDictionary<string, CommitSignatureVerification> _definitive = new(StringComparer.Ordinal);

    /// <summary>Process-wide instance for callers outside dependency injection.</summary>
    public static ViberailsCommitSignatureVerifier Shared => SharedInstance.Value;

    public ViberailsCommitSignatureVerifier()
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }), ownsClient: true)
    {
    }

    // Tests substitute the transport and a loopback endpoint; production always uses VerifyUrl.
    internal ViberailsCommitSignatureVerifier(HttpClient client, bool ownsClient = false, Uri? endpoint = null)
    {
        _client = client;
        _ownsClient = ownsClient;
        _endpoint = endpoint ?? new Uri(VerifyUrl);
    }

    public async Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken)
    {
        var cacheKey = $"{signature.KeyId:N}|{signature.Fingerprint}|{signature.TreeId}|{signature.SignatureBase64}";
        if (_definitive.TryGetValue(cacheKey, out var cached)) return cached;

        CommitSignatureVerification result;
        try
        {
            result = await SendAsync(signature, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or InvalidOperationException or NotSupportedException or IOException)
        {
            // Never relay exception text: it can carry proxy or DNS details the user did not ask to see.
            return new(CommitSignatureStatus.Unavailable, "viberails.ai could not be reached");
        }

        if (result.Status != CommitSignatureStatus.Unavailable) _definitive[cacheKey] = result;
        return result;
    }

    private async Task<CommitSignatureVerification> SendAsync(CommitSignature signature, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = JsonContent.Create(
            new VerifySignatureRequest(
                signature.KeyId,
                Convert.ToBase64String(CommitSignatureFormat.BuildPayload(signature.TreeId)),
                signature.SignatureBase64,
                CommitSignatureFormat.Algorithm,
                signature.Fingerprint),
            AppJsonSerializerContext.Default.VerifySignatureRequest);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(CommitSignatureStatus.Unavailable, "viberails.ai is rate limiting verification; retry in a minute");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            return new(CommitSignatureStatus.Rejected, "viberails.ai rejected the signature as malformed");
        if (!response.IsSuccessStatusCode)
            return new(CommitSignatureStatus.Unavailable, $"viberails.ai returned HTTP {(int)response.StatusCode}");

        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token);
        var body = await response.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.PublicSignatureVerificationResponse, timeout.Token);
        return Interpret(signature, body);
    }

    internal static CommitSignatureVerification Interpret(CommitSignature signature, PublicSignatureVerificationResponse? body)
    {
        if (body is null || body.KeyId != signature.KeyId)
            return new(CommitSignatureStatus.Unavailable, "viberails.ai returned an unexpected verification response");

        if (body.Valid && !string.IsNullOrWhiteSpace(body.SignerEmail))
        {
            // The fingerprint was sent, so the server already cross-checked it; a mismatch here
            // means the response is not about the key we asked about.
            if (body.Fingerprint is not null && !string.Equals(body.Fingerprint, signature.Fingerprint, StringComparison.Ordinal))
                return new(CommitSignatureStatus.Unavailable, "viberails.ai returned an unexpected verification response");
            return new(CommitSignatureStatus.Valid, "valid", body.SignerEmail.Trim());
        }

        return body.Reason switch
        {
            "signer_email_unconfirmed" => new(CommitSignatureStatus.Unavailable,
                "viberails.ai could not confirm the signer's email right now"),
            "signer_email_unverified" => new(CommitSignatureStatus.Rejected,
                "the signing key's viberails.ai account has no verified email"),
            "key_not_found" => new(CommitSignatureStatus.Rejected,
                "the signing key is not registered on viberails.ai (sync it in Settings > KEYS)"),
            "key_revoked" => new(CommitSignatureStatus.Rejected, "the signing key has been revoked"),
            "fingerprint_mismatch" => new(CommitSignatureStatus.Rejected,
                "the key fingerprint does not match the registered key"),
            "signature_mismatch" => new(CommitSignatureStatus.Rejected,
                "the signature does not match this commit's staged content"),
            _ => new(CommitSignatureStatus.Rejected, "viberails.ai did not accept the signature")
        };
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
