using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.SigningKeys;
using Xunit;

namespace Tests.Services.SigningKeys;

public sealed class SigningKeyServiceTests : IDisposable
{
    private const string Password = " 1234-🔑 ";
    private readonly string _testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "signing-key-service-tests"));
    private readonly string _directory;
    private readonly CapturingHandler _handler = new();
    private readonly HttpClient _client;
    private readonly SigningKeyStore _store;
    private readonly ManualClock _clock = new();
    private readonly SigningKeyService _service;
    private string _apiKey = "";

    public SigningKeyServiceTests()
    {
        // A per-test directory under the test output keeps cleanup exact; ancestor symlinks
        // (notably /var on macOS) are tolerated by the store, only the key folder itself is checked.
        _directory = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        _client = new HttpClient(_handler);
        _store = new SigningKeyStore(_directory);
        _service = new SigningKeyService(_client, _store, () => _apiKey, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_ProducesRsa4096AndEncryptedPkcs8_WithoutUploadingWhenNoApiKey()
    {
        var result = await _service.CreateAsync(new("  Signing key  ", Password), Ct);

        Assert.Equal("not_configured", result.SyncStatus);
        Assert.Null(result.SyncError);
        Assert.Equal("Signing key", result.Key.Name);
        Assert.Null(result.Key.CloudKeyId);
        Assert.Empty(_handler.Requests);
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(result.Key.PublicKeyPem);
        Assert.Equal(4096, publicKey.KeySize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo())), result.Key.Fingerprint);

        var backup = await _service.ExportAsync(result.Key.Id, Password, Ct);
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", backup.EncryptedPrivateKeyPem);
        using var decrypted = RSA.Create();
        decrypted.ImportFromEncryptedPem(backup.EncryptedPrivateKeyPem, Password);
        Assert.Equal(4096, decrypted.KeySize);
        Assert.Equal(publicKey.ExportSubjectPublicKeyInfo(), decrypted.ExportSubjectPublicKeyInfo());
        Assert.Equal(result.Key.PublicKeyPem, backup.PublicKeyPem);

        using var wrongPassword = RSA.Create();
        Assert.Throws<CryptographicException>(() => wrongPassword.ImportFromEncryptedPem(backup.EncryptedPrivateKeyPem, "wrong-password"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ExportAsync(result.Key.Id, "wrong-password", Ct));
        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(KeyPath(result.Key.Id), Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\t\r\n ")]
    [InlineData("abc")]
    [InlineData("abcdefg")]
    [InlineData("🔑🔑🔑🔑🔑🔑🔑")]
    [InlineData("12345678")]
    [InlineData(" 1234 5678 ")]
    [InlineData("１２３４５６７８")] // full-width digits are still digits
    public async Task Create_RejectsBlankShortOrDigitOnlyPasswords_BeforeWritingOrSending(string? password)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new("Key", password), Ct));
        Assert.False(Directory.Exists(_directory));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void PasswordValidation_CountsUnicodeScalars_DoesNotTrim_AndRefusesDigitOnlyPins()
    {
        SigningKeyService.ValidatePassword("abcdefgh");
        SigningKeyService.ValidatePassword("1234567a");
        SigningKeyService.ValidatePassword("🔑🔑🔑🔑🔑🔑🔑🔑");
        SigningKeyService.ValidatePassword(string.Concat(Enumerable.Repeat("🔑", 128)));
        SigningKeyService.ValidatePassword(" abcdefg ");
        SigningKeyService.ValidatePassword("correct horse battery staple");
        Assert.Throws<ArgumentException>(() => SigningKeyService.ValidatePassword("1234"));
        Assert.Throws<ArgumentException>(() => SigningKeyService.ValidatePassword("abcdefg"));
        Assert.Throws<ArgumentException>(() => SigningKeyService.ValidatePassword("12345678"));
        Assert.Throws<ArgumentException>(() => SigningKeyService.ValidatePassword("1234 5678 9012"));
        Assert.Throws<ArgumentException>(() => SigningKeyService.ValidatePassword(new string('a', 129)));
        Assert.Throws<ArgumentException>(() => SigningKeyService.ValidatePassword(string.Concat(Enumerable.Repeat("🔑", 129))));
    }

    [Fact]
    public async Task Sign_VerifiesExactPayloadWithPssSha256_AndRejectsTampering()
    {
        var created = await _service.CreateAsync(new("Signing key", Password), Ct);
        var payload = Encoding.UTF8.GetBytes("  exact content\r\n🔑\0 ");
        var signed = await _service.SignAsync(created.Key.Id, new(Password, Convert.ToBase64String(payload)), Ct);

        Assert.Equal("RSA-PSS-SHA256", signed.Algorithm);
        Assert.Equal(created.Key.Fingerprint, signed.Fingerprint);
        Assert.Equal(created.Key.PublicKeyPem, signed.PublicKeyPem); // travels with the signature for offline checks
        Assert.Null(signed.KeyId);
        Assert.Equal(payload, Convert.FromBase64String(signed.PayloadBase64));
        using var verifier = RSA.Create();
        verifier.ImportFromPem(created.Key.PublicKeyPem);
        var signature = Convert.FromBase64String(signed.SignatureBase64);
        Assert.Equal(512, signature.Length);
        Assert.True(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.False(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        payload[0] ^= 1;
        Assert.False(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        payload[0] ^= 1;
        signature[0] ^= 1;
        Assert.False(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SignAsync(created.Key.Id, new("wrong-password", "AA=="), Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("%%%")]
    public async Task Sign_RejectsMalformedPayloadBeforeAccessingPrivateKeys(string? payload)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SignAsync(Guid.NewGuid(), new(Password, payload), Ct));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Sign_RejectsOversizedPayloadBeforeAccessingPrivateKeys()
    {
        var oversized = Convert.ToBase64String(new byte[SigningKeyService.MaxPayloadBytes + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SignAsync(Guid.NewGuid(), new(Password, oversized), Ct));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Restart_PreservesEncryptedKey_AndPublicDtosContainNoPrivateMaterialOrPassword()
    {
        var created = await _service.CreateAsync(new("Persistent key", Password), Ct);
        var originalBackup = await _service.ExportAsync(created.Key.Id, Password, Ct);
        var restarted = new SigningKeyService(_client, new SigningKeyStore(_directory), () => _apiKey);

        var listed = await restarted.ListAsync(Ct);
        Assert.Equal(created.Key, Assert.Single(listed.Keys));
        Assert.Empty(listed.Warnings);
        var backup = await restarted.ExportAsync(created.Key.Id, Password, Ct);
        Assert.Equal(originalBackup, backup);
        var publicJson = JsonSerializer.Serialize(listed, AppJsonSerializerContext.Default.SigningKeyListResponse);
        Assert.DoesNotContain("private", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cloudCredentialHash", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, publicJson);
        Assert.DoesNotContain(originalBackup.EncryptedPrivateKeyPem, publicJson);
        var storedJson = await File.ReadAllTextAsync(KeyPath(created.Key.Id), Ct);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", storedJson);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", storedJson);
        Assert.DoesNotContain(Password, storedJson);
    }

    [Fact]
    public async Task Register_SendsPublicMaterialAndProof_WithApiKeyHeader_AndTracksCredentialChanges()
    {
        _apiKey = "first-test-api-key";
        var firstCloudId = Guid.NewGuid();
        _handler.Respond = captured => RegistrationReply(captured, firstCloudId);

        var created = await _service.CreateAsync(new("Work key", Password), Ct);
        Assert.Equal("synced", created.SyncStatus);
        Assert.Equal(firstCloudId, created.Key.CloudKeyId);
        Assert.NotNull(created.Key.CloudSyncedUtc);
        Assert.Equal(2, _handler.Requests.Count);
        var challengeSent = _handler.Requests[0];
        Assert.Equal("https://viberails.ai/api/v1/signing-keys/challenge", challengeSent.Uri);
        Assert.Equal("first-test-api-key", challengeSent.ApiKey);
        using var challengeBody = JsonDocument.Parse(challengeSent.Body);
        Assert.Equal(["publicKeyPem"], challengeBody.RootElement.EnumerateObject().Select(item => item.Name));
        var sent = _handler.Requests[1];
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://viberails.ai/api/v1/signing-keys", sent.Uri);
        Assert.Equal("first-test-api-key", sent.ApiKey);
        Assert.Equal("application/json", sent.ContentType);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(["challengeId", "name", "proofSignatureBase64", "publicKeyPem"], body.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Work key", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(created.Key.PublicKeyPem, body.RootElement.GetProperty("publicKeyPem").GetString());
        Assert.DoesNotContain("private", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, sent.Body);
        Assert.DoesNotContain(_apiKey, sent.Body);
        Assert.DoesNotContain(_apiKey, await File.ReadAllTextAsync(KeyPath(created.Key.Id), Ct));

        _apiKey = "second-test-api-key";
        var changedAccount = Assert.Single((await _service.ListAsync(Ct)).Keys);
        Assert.Null(changedAccount.CloudKeyId);
        Assert.Null(changedAccount.CloudSyncedUtc);
        var signed = await _service.SignAsync(created.Key.Id, new(Password, "AA=="), Ct);
        Assert.Null(signed.KeyId);
        Assert.Equal(2, _handler.Requests.Count);

        var secondCloudId = Guid.NewGuid();
        _handler.Respond = captured => RegistrationReply(captured, secondCloudId);
        var synced = await _service.SyncAsync(created.Key.Id, Password, Ct);
        Assert.Equal("synced", synced.SyncStatus);
        Assert.Equal(secondCloudId, synced.Key.CloudKeyId);
        Assert.Equal("second-test-api-key", _handler.Requests.Last().ApiKey);
        Assert.Equal(secondCloudId, Assert.Single((await _service.ListAsync(Ct)).Keys).CloudKeyId);

        _apiKey = "";
        var noCredential = await _service.ListAsync(Ct);
        Assert.False(noCredential.ApiKeyConfigured);
        Assert.Null(Assert.Single(noCredential.Keys).CloudKeyId);
        Assert.Null(Assert.Single(noCredential.Keys).CloudSyncedUtc);
    }

    [Fact]
    public async Task RegistrationOutage_RetainsLocalKey_AndRetryPublishesTheSamePublicKey()
    {
        _apiKey = "outage-test-api-key";
        _handler.Respond = _ => throw new HttpRequestException($"Sensitive upstream exception {_apiKey} {Password}");
        var created = await _service.CreateAsync(new("Offline key", Password), Ct);
        Assert.Equal("failed", created.SyncStatus);
        Assert.Null(created.Key.CloudKeyId);
        Assert.DoesNotContain(_apiKey, created.SyncError!);
        Assert.DoesNotContain(Password, created.SyncError!);
        Assert.Equal(created.Key, Assert.Single((await _service.ListAsync(Ct)).Keys));
        var originalBackup = await _service.ExportAsync(created.Key.Id, Password, Ct);

        var cloudId = Guid.NewGuid();
        _handler.Respond = captured => RegistrationReply(captured, cloudId);
        var retried = await _service.SyncAsync(created.Key.Id, Password, Ct);
        Assert.Equal("synced", retried.SyncStatus);
        Assert.Equal(cloudId, retried.Key.CloudKeyId);
        Assert.Equal(created.Key.Id, retried.Key.Id);
        Assert.Equal(created.Key.PublicKeyPem, retried.Key.PublicKeyPem);
        Assert.Equal(created.Key.Fingerprint, retried.Key.Fingerprint);
        Assert.Equal(originalBackup, await _service.ExportAsync(created.Key.Id, Password, Ct));
        Assert.Equal(4, _handler.Requests.Count);
        Assert.Equal(_handler.Requests[0].Body, _handler.Requests[2].Body);
        // Each retry must get a fresh proof rather than replay the old challenge signature.
        Assert.NotEqual(_handler.Requests[1].Body, _handler.Requests[3].Body);
    }

    [Fact]
    public async Task InvalidRegistrationReplies_NeverMarkTheKeySynced_OrExposeUpstreamContent()
    {
        var created = await _service.CreateAsync(new("Response validation", Password), Ct);
        _apiKey = "response-test-api-key";
        var invalidReplies = new Func<CapturedRegistration, HttpResponseMessage>[]
        {
            _ => JsonReply("null"),
            _ => JsonReply("{}"),
            _ => JsonReply("{not-json}"),
            captured => RegistrationReply(captured, Guid.Empty),
            _ => JsonReply(JsonSerializer.Serialize(new { id = Guid.NewGuid(), fingerprint = new string('0', 64) })),
            _ => JsonReply(new string('x', 17 * 1024)),
            _ => JsonReply($"private response {_apiKey} {Password}", HttpStatusCode.Unauthorized)
        };

        foreach (var reply in invalidReplies)
        {
            _handler.Respond = reply;
            var result = await _service.SyncAsync(created.Key.Id, Password, Ct);
            Assert.Equal("failed", result.SyncStatus);
            Assert.Null(result.Key.CloudKeyId);
            Assert.Null(result.Key.CloudSyncedUtc);
            Assert.DoesNotContain(_apiKey, result.SyncError!);
            Assert.DoesNotContain(Password, result.SyncError!);
            Assert.Null(Assert.Single((await _service.ListAsync(Ct)).Keys).CloudKeyId);
        }
    }

    [Fact]
    public async Task InvalidOrExpiredChallenges_AreNeverSignedOrRegistered()
    {
        var created = await _service.CreateAsync(new("Challenge validation", Password), Ct);
        _apiKey = "challenge-test-api-key";
        var mutations = new Func<SigningKeyChallengeResponse, SigningKeyChallengeResponse>[]
        {
            challenge => challenge with { ChallengeId = Guid.Empty },
            challenge => challenge with { ChallengeId = Guid.NewGuid() },
            challenge => challenge with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            challenge => challenge with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) },
            challenge => challenge with { PayloadBase64 = "%%%" },
            challenge => challenge with { PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Arbitrary content the server wants signed")) },
            challenge => challenge with { PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{SigningKeyService.ChallengePrefix}{challenge.ChallengeId:D}\n{new string('0', 64)}\n{Convert.ToBase64String(new byte[32])}\n")) },
            challenge => challenge with { PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{SigningKeyService.ChallengePrefix}{challenge.ChallengeId:D}\n{created.Key.Fingerprint}\n{Convert.ToBase64String(new byte[31])}\n")) },
            // Exactly the bound prefix with an empty nonce: passes the prefix and trailing-newline
            // checks, and used to make the nonce slice throw ArgumentOutOfRangeException.
            challenge => challenge with { PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{SigningKeyService.ChallengePrefix}{challenge.ChallengeId:D}\n{created.Key.Fingerprint}\n")) }
        };

        foreach (var mutate in mutations)
        {
            _handler.ChallengeRespond = captured => JsonReply(JsonSerializer.Serialize(mutate(ChallengeFor(captured)),
                AppJsonSerializerContext.Default.SigningKeyChallengeResponse));
            var result = await _service.SyncAsync(created.Key.Id, Password, Ct);
            Assert.Equal("failed", result.SyncStatus);
            Assert.Null(result.Key.CloudKeyId);
        }
        Assert.Equal(mutations.Length, _handler.Requests.Count);
        Assert.All(_handler.Requests, request => Assert.EndsWith("/challenge", request.Uri));
    }

    [Fact]
    public async Task Sync_WrongPassword_NeverRequestsAChallenge()
    {
        var created = await _service.CreateAsync(new("Owned key", Password), Ct);
        _apiKey = "ownership-test-api-key";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SyncAsync(created.Key.Id, "wrong-password", Ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Unlock_RejectsStoredFingerprintMismatch_EvenWithTheCorrectPassword()
    {
        var created = await _service.CreateAsync(new("Integrity key", Password), Ct);
        using (await _store.LockAsync(Ct))
        {
            var stored = _store.Read(created.Key.Id);
            _store.Write(stored with { Fingerprint = new string('0', 64) });
        }
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ExportAsync(created.Key.Id, Password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SignAsync(created.Key.Id, new(Password, "AA=="), Ct));
    }

    [Fact]
    public async Task Unlock_LocksAfterFiveFailures_RefusesTheCorrectPasswordUntilTheWindowPasses_AndResetsOnSuccess()
    {
        var created = await _service.CreateAsync(new("Locked key", Password), Ct);
        for (var attempt = 0; attempt < SigningKeyService.MaxUnlockFailures; attempt++)
            await Assert.ThrowsAsync<ArgumentException>(() => _service.ExportAsync(created.Key.Id, "wrong-password", Ct));
        // Locked: every unlocking operation refuses, even with the right password, and none reaches the network.
        await Assert.ThrowsAsync<SigningKeyLockedException>(() => _service.ExportAsync(created.Key.Id, Password, Ct));
        await Assert.ThrowsAsync<SigningKeyLockedException>(() => _service.SignAsync(created.Key.Id, new(Password, "AA=="), Ct));
        _apiKey = "lockout-test-api-key";
        await Assert.ThrowsAsync<SigningKeyLockedException>(() => _service.SyncAsync(created.Key.Id, Password, Ct));
        _apiKey = "";
        Assert.Empty(_handler.Requests);
        // One second short of the window is still locked; the window itself reopens the key.
        _clock.Now += SigningKeyService.UnlockFailureWindow - TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<SigningKeyLockedException>(() => _service.ExportAsync(created.Key.Id, Password, Ct));
        _clock.Now += TimeSpan.FromSeconds(1);
        var backup = await _service.ExportAsync(created.Key.Id, Password, Ct);
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", backup.EncryptedPrivateKeyPem);
        // Success cleared the counter: a full set of failures is needed before it locks again.
        for (var attempt = 0; attempt < SigningKeyService.MaxUnlockFailures; attempt++)
            await Assert.ThrowsAsync<ArgumentException>(() => _service.ExportAsync(created.Key.Id, "wrong-password", Ct));
        await Assert.ThrowsAsync<SigningKeyLockedException>(() => _service.ExportAsync(created.Key.Id, Password, Ct));
        // The counter is per key, and per backend process (documented limitation: a restart clears it).
        var other = await _service.CreateAsync(new("Other key", Password), Ct);
        await Assert.ThrowsAsync<SigningKeyLockedException>(() => _service.ExportAsync(created.Key.Id, Password, Ct));
        Assert.NotNull(await _service.ExportAsync(other.Key.Id, Password, Ct));
        var restarted = new SigningKeyService(_client, new SigningKeyStore(_directory), () => _apiKey, _clock);
        Assert.Equal(backup, await restarted.ExportAsync(created.Key.Id, Password, Ct));
    }

    [Fact]
    public async Task List_SkipsStrayOrDamagedFiles_ReportsThemByName_AndKeepsCreateWorking()
    {
        var created = await _service.CreateAsync(new("Good key", Password), Ct);
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.json"), "{}", Ct);
        var damagedId = Guid.NewGuid();
        await File.WriteAllTextAsync(KeyPath(damagedId), $"{{\"id\":\"{damagedId:N}\"", Ct); // truncated JSON

        var listed = await _service.ListAsync(Ct);
        Assert.Equal(created.Key, Assert.Single(listed.Keys));
        Assert.Equal(2, listed.Warnings.Length);
        Assert.Contains(listed.Warnings, warning => warning.Contains("notes.json", StringComparison.Ordinal));
        Assert.Contains(listed.Warnings, warning => warning.Contains($"{damagedId:N}.json", StringComparison.Ordinal));
        Assert.All(listed.Warnings, warning => Assert.DoesNotContain(_directory, warning));
        // The single-key paths still surface the damage instead of pretending the key works.
        await Assert.ThrowsAsync<JsonException>(() => _service.ExportAsync(damagedId, Password, Ct));
        // Creation counts only readable keys, so the damaged file cannot block new ones.
        await _service.CreateAsync(new("Second key", Password), Ct);
        Assert.Equal(2, (await _service.ListAsync(Ct)).Keys.Length);
    }

    private string KeyPath(Guid id) => Path.Combine(_directory, $"{id:N}.json");

    private static HttpResponseMessage RegistrationReply(CapturedRegistration captured, Guid id)
    {
        using var body = JsonDocument.Parse(captured.Body);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(body.RootElement.GetProperty("publicKeyPem").GetString());
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        return JsonReply(JsonSerializer.Serialize(new RegisteredSigningKeyResponse(id, fingerprint),
            AppJsonSerializerContext.Default.RegisteredSigningKeyResponse));
    }

    private static SigningKeyChallengeResponse ChallengeFor(CapturedRegistration captured)
    {
        using var body = JsonDocument.Parse(captured.Body);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(body.RootElement.GetProperty("publicKeyPem").GetString());
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        var challengeId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes($"VibeRails signing-key registration v1\n{challengeId:D}\n{fingerprint}\n{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}\n");
        return new(challengeId, Convert.ToBase64String(payload), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static HttpResponseMessage JsonReply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        _client.Dispose();
        var resolved = Path.GetFullPath(_directory);
        // Never recursively remove anything except this test's exact generated child.
        if (Path.GetDirectoryName(resolved) != _testRoot || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Unexpected signing-key test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    /// <summary>Starts at the real clock so server-issued challenge expiries stay plausible; only the lockout test advances it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record CapturedRegistration(HttpMethod Method, string? Uri, string? ApiKey, string? ContentType, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRegistration> Requests { get; } = [];
        public Dictionary<Guid, byte[]> Challenges { get; } = [];
        public Func<CapturedRegistration, HttpResponseMessage>? ChallengeRespond { get; set; }
        public Func<CapturedRegistration, HttpResponseMessage> Respond { get; set; } = _ => JsonReply("{}", HttpStatusCode.ServiceUnavailable);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRegistration(request.Method, request.RequestUri?.AbsoluteUri,
                request.Headers.TryGetValues("X-Api-Key", out var values) ? Assert.Single(values) : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            if (captured.Uri?.EndsWith("/challenge", StringComparison.Ordinal) == true)
            {
                if (ChallengeRespond is not null) return ChallengeRespond(captured);
                var challenge = ChallengeFor(captured);
                Challenges.Add(challenge.ChallengeId, Convert.FromBase64String(challenge.PayloadBase64));
                return JsonReply(JsonSerializer.Serialize(challenge, AppJsonSerializerContext.Default.SigningKeyChallengeResponse));
            }
            using var body = JsonDocument.Parse(captured.Body);
            using var verifier = RSA.Create();
            verifier.ImportFromPem(body.RootElement.GetProperty("publicKeyPem").GetString());
            var challengeId = body.RootElement.GetProperty("challengeId").GetGuid();
            var signature = Convert.FromBase64String(body.RootElement.GetProperty("proofSignatureBase64").GetString()!);
            Assert.True(verifier.VerifyData(Challenges[challengeId], signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
                "Registration must prove possession by signing the exact one-time challenge.");
            return Respond(captured);
        }
    }
}
