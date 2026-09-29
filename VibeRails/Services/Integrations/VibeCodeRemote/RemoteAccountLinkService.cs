using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Links the existing local API key setting by outbound device polling. Secrets stay in the
/// backend. Each attempt owns its network work, so a disconnected browser cannot lose a key.
/// </summary>
public sealed class RemoteAccountLinkService : IDisposable
{
    private const int MaxBodyBytes = 16 * 1024;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly IRemoteAccountKeyStore _keys;
    private readonly IAppEventBus _events;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private Attempt? _attempt;
    private RemoteAccountLinkStatus _status = new("idle");
    private bool _disposed;
    private string? _linkedKeyFingerprint;

    /// <summary>Creates a linker for an HTTPS origin (HTTP is allowed only on loopback).</summary>
    public RemoteAccountLinkService(HttpClient client, Uri endpoint, IRemoteAccountKeyStore keyStore,
        IAppEventBus events, TimeProvider? clock = null)
    {
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath != "/")
            throw new ArgumentException("Account linking requires an HTTPS origin.", nameof(endpoint));
        _client = client;
        _endpoint = endpoint;
        _keys = keyStore;
        _events = events;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Starts a new request and invalidates any earlier attempt.</summary>
    public async Task<RemoteAccountLinkStatus> StartAsync()
    {
        Attempt attempt;
        Attempt? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _attempt;
            attempt = new Attempt(_keys.Read());
            _attempt = attempt;
            _status = new("pending");
            _linkedKeyFingerprint = null;
        }
        previous?.Cancellation.Cancel();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "/api/v1/device-links"))
            {
                Content = JsonContent.Create(new DeviceLinkRequest(_keys.ComputerName,
                    typeof(RemoteAccountLinkService).Assembly.GetName().Version?.ToString() ?? "unknown"),
                    AppJsonSerializerContext.Default.DeviceLinkRequest)
            };
            using var deadline = Deadline(attempt);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Finish(attempt, new("unavailable", Error: "old_server"));
            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
                return Finish(attempt, new("error", Error: "remote_error"));
            var created = await ReadAsync(response, AppJsonSerializerContext.Default.DeviceLinkCreated, deadline.Token);
            var verification = new Uri(_endpoint, "/link");
            if (!SecretValid(created.DeviceCode, 32) || string.IsNullOrWhiteSpace(created.UserCode)
                || created.UserCode.Length > 16 || !created.UserCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                || !Uri.TryCreate(created.VerificationUri, UriKind.Absolute, out var supplied)
                || supplied != verification || supplied.UserInfo.Length != 0 || supplied.Query.Length != 0
                || supplied.Fragment.Length != 0 || created.ExpiresIn is < 1 or > 600 || created.Interval is < 1 or > 60)
                throw new JsonException();
            lock (_gate)
            {
                if (!Current(attempt)) return _status;
                attempt.DeviceCode = created.DeviceCode;
                attempt.ExpiresAt = _clock.GetUtcNow().AddSeconds(created.ExpiresIn);
                attempt.NextPoll = _clock.GetUtcNow().AddSeconds(created.Interval);
                _status = new("pending", created.UserCode, verification.AbsoluteUri, attempt.ExpiresAt, created.Interval);
                return _status;
            }
        }
        catch (JsonException) { return Finish(attempt, new("error", Error: "invalid_response")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { return Finish(attempt, new("error", Error: "remote_error")); }
    }

    /// <summary>Returns progress and performs at most one token request per polling interval.</summary>
    public Task<RemoteAccountLinkStatus> PollAsync()
    {
        lock (_gate)
        {
            if (_status.Status == "linked" && _linkedKeyFingerprint != Fingerprint(_keys.Read()))
                _status = new("idle");
            if (_attempt is not { } attempt || _status.Status != "pending" || attempt.DeviceCode is null)
                return Task.FromResult(_status);
            if (attempt.Poll is { IsCompleted: false }) return attempt.Poll;
            if (attempt.Token is not null) return Task.FromResult(Save(attempt));
            if (!string.Equals(_keys.Read(), attempt.OriginalKey, StringComparison.Ordinal))
                return Task.FromResult(Finish(attempt, new("error", Error: "key_changed")));
            if (_clock.GetUtcNow() >= attempt.ExpiresAt)
                return Task.FromResult(Finish(attempt, new("expired", Error: "expired_token")));
            if (_clock.GetUtcNow() < attempt.NextPoll) return Task.FromResult(_status);
            attempt.NextPoll = _clock.GetUtcNow().AddSeconds(_status.Interval);
            attempt.Poll = PollCoreAsync(attempt);
            return attempt.Poll;
        }
    }

    private async Task<RemoteAccountLinkStatus> PollCoreAsync(Attempt attempt)
    {
        try
        {
            using var message = SecretRequest(HttpMethod.Post, "/api/v1/device-links/token", attempt.DeviceCode!);
            using var deadline = Deadline(attempt);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var token = await ReadAsync(response, AppJsonSerializerContext.Default.DeviceLinkToken, deadline.Token);
                if (!SecretValid(token.ApiKey, 16) || token.Account?.Email?.Length > 320 || token.Account?.Name?.Length > 200)
                    throw new JsonException();
                lock (_gate)
                {
                    if (!Current(attempt)) return _status;
                    attempt.Token = token;
                    return Save(attempt);
                }
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.NotFound)
                return Finish(attempt, response.StatusCode == HttpStatusCode.Forbidden
                    ? new("denied", Error: "access_denied") : new("expired", Error: "expired_token"));
            lock (_gate)
            {
                if (!Current(attempt)) return _status;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var interval = Math.Min(60, _status.Interval + 5);
                    attempt.NextPoll = _clock.GetUtcNow().AddSeconds(interval);
                    _status = _status with { Interval = interval, Error = null };
                }
                else _status = _status with { Error = response.StatusCode == HttpStatusCode.Accepted ? null : "remote_error" };
                return _status;
            }
        }
        catch (JsonException) { return Finish(attempt, new("error", Error: "invalid_response")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            lock (_gate)
            {
                if (Current(attempt)) _status = _status with { Error = "remote_error" };
                return _status;
            }
        }
    }

    // Caller holds the attempt gate through the compare/save. Cancel or replacement cannot
    // return successfully and then have this response overwrite a later credential.
    private RemoteAccountLinkStatus Save(Attempt attempt)
    {
        if (!Current(attempt)) return _status;
        var token = attempt.Token!;
        bool saved;
        try { saved = _keys.TrySave(token.ApiKey, attempt.OriginalKey); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = _status with { Error = "save_failed", ExpiresAt = null };
            return _status; // Keep the received key in memory; do not redeem it again.
        }
        if (!saved) return Finish(attempt, new("error", Error: "key_changed"));
        _linkedKeyFingerprint = Fingerprint(token.ApiKey);
        var result = Finish(attempt, new("linked", KeyHint: new string('•', token.ApiKey.Length - 4) + token.ApiKey[^4..], Account: token.Account));
        // A subscriber failure cannot turn a successful persisted key into a failed sign-in.
        try { _events.Publish("remote-account-linked", result, AppJsonSerializerContext.Default.RemoteAccountLinkStatus); }
        catch { }
        return result;
    }

    /// <summary>Cancels immediately locally and best-effort expires the remote pending request.</summary>
    public async Task<RemoteAccountLinkStatus> CancelAsync()
    {
        Attempt? attempt;
        RemoteAccountLinkStatus result;
        lock (_gate)
        {
            // A save that already won the gate cannot be undone by cancellation. Report it
            // accurately even when the caller never received the poll response/event.
            if (_status.Status == "linked" && _linkedKeyFingerprint == Fingerprint(_keys.Read()))
                return _status;
            attempt = _attempt;
            _attempt = null;
            result = _status = new("cancelled");
            if (attempt is not null) attempt.Token = null;
        }
        attempt?.Cancellation.Cancel();
        if (attempt?.DeviceCode is { } code)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var message = SecretRequest(HttpMethod.Delete, "/api/v1/device-links", code);
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
        }
        return result;
    }

    private bool Current(Attempt attempt) => ReferenceEquals(_attempt, attempt) && !_disposed;

    private RemoteAccountLinkStatus Finish(Attempt attempt, RemoteAccountLinkStatus status)
    {
        lock (_gate)
        {
            if (!Current(attempt)) return _status;
            attempt.Token = null;
            attempt.DeviceCode = null;
            _status = status;
            return status;
        }
    }

    private HttpRequestMessage SecretRequest(HttpMethod method, string path, string code) => new(method, new Uri(_endpoint, path))
    {
        Content = JsonContent.Create(new DeviceLinkSecret(code), AppJsonSerializerContext.Default.DeviceLinkSecret)
    };

    private static bool SecretValid(string? value, int minimum) => value is not null && value.Length >= minimum
        && value.Length <= 512 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static CancellationTokenSource Deadline(Attempt attempt)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
        source.CancelAfter(TimeSpan.FromSeconds(20));
        return source;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxBodyBytes) throw new JsonException();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > MaxBodyBytes) throw new JsonException();
            buffer.Write(chunk, 0, count);
        }
        return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), type) ?? throw new JsonException();
    }

    /// <summary>Stops in-flight operations when the root backend shuts down.</summary>
    public void Dispose()
    {
        Attempt? attempt;
        lock (_gate)
        {
            _disposed = true;
            attempt = _attempt;
            _attempt = null;
            if (attempt is not null) attempt.Token = null;
        }
        attempt?.Cancellation.Cancel();
    }

    private sealed class Attempt(string originalKey)
    {
        public string OriginalKey { get; } = originalKey;
        public CancellationTokenSource Cancellation { get; } = new();
        public string? DeviceCode { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset NextPoll { get; set; }
        public DeviceLinkToken? Token { get; set; }
        public Task<RemoteAccountLinkStatus>? Poll { get; set; }
    }
}
