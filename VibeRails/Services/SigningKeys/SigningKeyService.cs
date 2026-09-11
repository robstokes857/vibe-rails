using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.SigningKeys;

public sealed class SigningKeyService(HttpClient cloudClient, SigningKeyStore store, Func<string> apiKey, TimeProvider? timeProvider = null)
{
    public const int KeySize = 4096;
    public const int MaxKeys = 50;
    public const int MaxPayloadBytes = 64 * 1024;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxUnlockFailures = 5;
    public static readonly TimeSpan UnlockFailureWindow = TimeSpan.FromMinutes(1);
    public const string Algorithm = "RSA-PSS-SHA256";
    public const string RegistrationUrl = "https://viberails.ai/api/v1/signing-keys";
    public const string ChallengePrefix = "VibeRails signing-key registration v1\n";
    private static readonly PbeParameters Protection = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    // Access is serialized by the cross-process store lock. Never remember passwords or unlocked keys.
    private readonly Dictionary<Guid, (int Count, DateTimeOffset Since)> _failures = [];

    public async Task<SigningKeyListResponse> ListAsync(CancellationToken ct = default)
    {
        using var lease = await store.LockAsync(ct);
        var credential = apiKey();
        var listing = store.ReadAll();
        return new(listing.Keys.Select(key => PublicInfo(key, credential)).ToArray(),
            !string.IsNullOrWhiteSpace(credential), listing.Warnings);
    }

    public async Task<SigningKeyMutationResponse> CreateAsync(CreateSigningKeyRequest request, CancellationToken ct = default)
    {
        ValidatePassword(request.Password);
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Key name must contain 1–80 characters without control characters.");
        using var lease = await store.LockAsync(ct);
        if (store.ReadAll().Keys.Length >= MaxKeys) throw new ArgumentException($"At most {MaxKeys} signing keys can be stored on this computer.");
        using var rsa = RSA.Create(KeySize);
        var key = new StoredSigningKey(Guid.NewGuid(), name, rsa.ExportSubjectPublicKeyInfoPem(),
            Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), _clock.GetUtcNow(),
            rsa.ExportEncryptedPkcs8PrivateKeyPem(request.Password.AsSpan(), Protection));
        // Persist before attempting the network. A cloud outage cannot lose the generated key.
        store.Write(key);
        return await RegisterAsync(key, rsa, ct);
    }

    public async Task<SigningKeyMutationResponse> SyncAsync(Guid id, string? password, CancellationToken ct = default)
    {
        using var lease = await store.LockAsync(ct);
        var key = store.Read(id);
        using var rsa = Unlock(key, password);
        return await RegisterAsync(key, rsa, ct);
    }

    public async Task<SigningKeyExportResponse> ExportAsync(Guid id, string? password, CancellationToken ct = default)
    {
        using var lease = await store.LockAsync(ct);
        var key = store.Read(id);
        using var rsa = Unlock(key, password);
        return new($"viberails-{id:N}-private.pem", key.EncryptedPrivateKeyPem, key.PublicKeyPem);
    }

    public async Task<SignedPayloadResponse> SignAsync(Guid id, SignPayloadRequest request, CancellationToken ct = default)
    {
        var payload = DecodePayload(request.PayloadBase64);
        // A registration challenge is a claim of account ownership, not an ordinary message.
        // Only the dedicated sync flow may sign it using this installation's API credential.
        if (payload.AsSpan().StartsWith(Encoding.UTF8.GetBytes(ChallengePrefix)))
            throw new ArgumentException("This content is reserved for public-key registration. Use Sync public key instead.");
        using var lease = await store.LockAsync(ct);
        var key = store.Read(id);
        using var rsa = Unlock(key, request.Password);
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new(PublicInfo(key, apiKey()).CloudKeyId, key.Fingerprint, key.PublicKeyPem, Algorithm,
            Convert.ToBase64String(payload), Convert.ToBase64String(signature));
    }

    /// <summary>
    /// 8–128 Unicode scalar values, not blank, and not digits alone. Password entropy is the only
    /// protection for a copied key file or an exported backup, so a short numeric PIN is refused.
    /// The password is used exactly as received: this build runs with invariant globalization, where
    /// <see cref="string.Normalize()"/> is a pass-through, so the dashboard normalizes to NFC before
    /// sending and any other caller must do the same or risk a key it cannot unlock elsewhere.
    /// </summary>
    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.EnumerateRunes().Count() is < MinPasswordLength or > MaxPasswordLength)
            throw new ArgumentException($"Password must contain {MinPasswordLength}–{MaxPasswordLength} characters and cannot be blank.");
        if (password.EnumerateRunes().All(rune => Rune.IsDigit(rune) || Rune.IsWhiteSpace(rune)))
            throw new ArgumentException("Password cannot be digits only. Use a passphrase of several words.");
    }

    private static byte[] DecodePayload(string? encoded)
    {
        if (encoded is null || encoded.Length > 4 * ((MaxPayloadBytes + 2) / 3))
            throw new ArgumentException("Payload must be base64 and at most 64 KiB.");
        try
        {
            var data = Convert.FromBase64String(encoded);
            if (data.Length > MaxPayloadBytes) throw new ArgumentException("Payload must be at most 64 KiB.");
            return data;
        }
        catch (FormatException) { throw new ArgumentException("Payload must be valid base64."); }
    }

    private RSA Unlock(StoredSigningKey key, string? password)
    {
        ValidatePassword(password);
        var now = _clock.GetUtcNow();
        var inWindow = _failures.TryGetValue(key.Id, out var failures) && now - failures.Since < UnlockFailureWindow;
        if (inWindow && failures.Count >= MaxUnlockFailures)
            throw new SigningKeyLockedException();
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(key.EncryptedPrivateKeyPem, password);
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            if (fingerprint != key.Fingerprint) throw new CryptographicException();
            _failures.Remove(key.Id);
            return rsa;
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            _failures[key.Id] = inWindow ? (failures.Count + 1, failures.Since) : (1, now);
            throw new ArgumentException("Incorrect password or damaged key file.");
        }
        catch { rsa.Dispose(); throw; }
    }

    private async Task<SigningKeyMutationResponse> RegisterAsync(StoredSigningKey key, RSA rsa, CancellationToken ct)
    {
        var credential = apiKey();
        if (string.IsNullOrWhiteSpace(credential)) return new(PublicInfo(key, credential), "not_configured", null);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var challengeRequest = new HttpRequestMessage(HttpMethod.Post, RegistrationUrl + "/challenge");
            challengeRequest.Headers.Add("X-Api-Key", credential);
            challengeRequest.Content = JsonContent.Create(new SigningKeyChallengeRequest(key.PublicKeyPem),
                AppJsonSerializerContext.Default.SigningKeyChallengeRequest);
            using var challengeResponse = await cloudClient.SendAsync(challengeRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!challengeResponse.IsSuccessStatusCode) return UploadFailure(key, credential, challengeResponse.StatusCode);
            await challengeResponse.Content.LoadIntoBufferAsync(16 * 1024, timeout.Token);
            var challenge = await challengeResponse.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.SigningKeyChallengeResponse, timeout.Token);
            var challengePayload = ValidateChallenge(challenge, key.Fingerprint, _clock.GetUtcNow());
            using var request = new HttpRequestMessage(HttpMethod.Post, RegistrationUrl);
            request.Headers.Add("X-Api-Key", credential);
            request.Content = JsonContent.Create(new RegisterSigningKeyRequest(key.Name, key.PublicKeyPem,
                challenge!.ChallengeId, Convert.ToBase64String(rsa.SignData(challengePayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))),
                AppJsonSerializerContext.Default.RegisterSigningKeyRequest);
            using var response = await cloudClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return UploadFailure(key, credential, response.StatusCode);
            // A compromised upstream must not stream an unbounded response into the desktop.
            await response.Content.LoadIntoBufferAsync(16 * 1024, timeout.Token);
            var registered = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.RegisteredSigningKeyResponse, timeout.Token);
            if (registered is null || registered.Id == Guid.Empty || registered.Fingerprint != key.Fingerprint)
                return new(PublicInfo(key, credential), "failed", "The server returned an invalid key registration. Your encrypted key is saved locally.");
            key = key with { CloudKeyId = registered.Id, CloudSyncedUtc = _clock.GetUtcNow(), CloudCredentialHash = CredentialHash(credential) };
            store.Write(key);
            return new(PublicInfo(key, credential), "synced", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException or FormatException)
        {
            // Never relay exception text, response bodies, or credentials into logging/UI.
            return new(PublicInfo(key, credential), "failed", "Public-key upload did not complete. Your encrypted key is saved locally; you can retry syncing.");
        }
    }

    private static SigningKeyMutationResponse UploadFailure(StoredSigningKey key, string credential, System.Net.HttpStatusCode status) =>
        new(PublicInfo(key, credential), "failed", status == System.Net.HttpStatusCode.Forbidden
            ? "Verify your account email on viberails.ai before syncing this public key. Your encrypted key is saved locally."
            : $"Public-key upload failed (HTTP {(int)status}). Your encrypted key is saved locally; check your API key and retry.");

    private static byte[] ValidateChallenge(SigningKeyChallengeResponse? challenge, string fingerprint, DateTimeOffset now)
    {
        if (challenge is null || challenge.ChallengeId == Guid.Empty || challenge.PayloadBase64 is null ||
            challenge.PayloadBase64.Length > 1024 || challenge.ExpiresUtc <= now || challenge.ExpiresUtc > now.AddMinutes(6))
            throw new FormatException("Invalid registration challenge.");
        var payload = Convert.FromBase64String(challenge.PayloadBase64);
        var prefix = $"{ChallengePrefix}{challenge.ChallengeId:D}\n{fingerprint}\n";
        var text = Encoding.UTF8.GetString(payload);
        // The length check comes first: a payload that is exactly the prefix would otherwise pass the
        // prefix and trailing-newline tests and make the nonce slice throw ArgumentOutOfRangeException,
        // which is not in RegisterAsync's catch list.
        if (text.Length <= prefix.Length || !text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('\n'))
            throw new FormatException("Invalid registration challenge.");
        var nonce = text[prefix.Length..^1];
        if (nonce.Length != 44 || Convert.FromBase64String(nonce).Length != 32 ||
            !payload.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text)))
            throw new FormatException("Invalid registration challenge.");
        return payload;
    }

    private static SigningKeyInfo PublicInfo(StoredSigningKey key, string credential)
    {
        var sameAccountCredential = !string.IsNullOrWhiteSpace(credential) && key.CloudCredentialHash == CredentialHash(credential);
        return new(key.Id, key.Name, key.PublicKeyPem, key.Fingerprint, key.CreatedUtc,
            sameAccountCredential ? key.CloudKeyId : null, sameAccountCredential ? key.CloudSyncedUtc : null);
    }

    private static string CredentialHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class SigningKeyLockedException : Exception
{
    public SigningKeyLockedException() : base("Too many incorrect attempts. Try again in one minute.") { }
}
