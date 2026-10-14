using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Utils;
using VibeRails.Security;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>The saved permission and credential for one title request.</summary>
public sealed record ConversationTitleSettings(bool Enabled, string ApiKey)
{
    /// <summary>Settings diagnostics must never print the credential.</summary>
    public override string ToString() => $"Conversation titles enabled: {Enabled}";

    internal static ConversationTitleSettings Read()
    {
        var settings = Config.LoadFresh();
        return new(settings.RemoteAccess, settings.ApiKey);
    }

    internal bool CanSend => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// A bounded, content-free failure result or a validated title. A <paramref name="KeyWide"/> failure
/// (rejected key, rate limit, unavailable endpoint, network) pauses every request for the API key;
/// any other failure belongs to this conversation and only delays its own retry.
/// </summary>
public sealed record ConversationTitleResult(string? Title, TimeSpan RetryAfter, bool Permanent = false,
    bool KeyWide = false, string? ExcerptHash = null, bool Unchanged = false);

/// <summary>Sends the opening of the redacted transcript to the fixed VibeRails title API.</summary>
public sealed class ConversationTitleClient
{
    internal const string HttpClientName = "conversation-titles";
    internal const string Endpoint = "https://viberails.ai/api/v1/conversation-title";
    internal const int MaxResponseBytes = 8192;
    /// <summary>
    /// The title API rejects more than 2,000 characters, so every full transcript was refused as
    /// permanent (VIBE-157). The owner chose to send the first 1,000 characters: the transcript opens
    /// with the first user prompt, which is what a title names.
    /// </summary>
    internal const int MaxExcerptChars = 1_000;
    private readonly HttpClient http;
    private readonly Func<ConversationTitleSettings> readSettings;
    private readonly TimeSpan timeout;

    /// <summary>Uses the current saved settings, including changes made by another root.</summary>
    public ConversationTitleClient(HttpClient http) : this(http, ConversationTitleSettings.Read) { }

    internal ConversationTitleClient(HttpClient http, Func<ConversationTitleSettings> readSettings,
        TimeSpan? timeout = null)
    {
        this.http = http;
        this.readSettings = readSettings;
        this.timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Reads current permission without loading or sending a transcript.</summary>
    public ConversationTitleSettings CurrentSettings => readSettings();

    /// <summary>
    /// Skips a request matching the last successfully applied excerpt hash.
    /// No remote response/error text, credentials or transcripts are logged.
    /// </summary>
    public async Task<ConversationTitleResult> GenerateAsync(string transcript,
        ConversationTitleSettings settings, CancellationToken ct, string? previousExcerptHash = null)
    {
        if (!settings.CanSend || settings != readSettings())
            return new(null, TimeSpan.FromMinutes(1));
        if (string.IsNullOrWhiteSpace(transcript))
            return new(null, TimeSpan.FromMinutes(1));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var monitorStop = new CancellationTokenSource();
        var monitor = WatchSettingsAsync(settings, deadline, monitorStop.Token);
        try
        {
            // Redact the entire transcript before taking the excerpt, so a secret is never cut
            // short of the context that lets the redactor recognize it.
            var text = Excerpt(CredentialRedactor.RedactTranscript(transcript, settings.ApiKey));
            var excerptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (settings != readSettings()) return new(null, TimeSpan.FromMinutes(1));
            if (excerptHash == previousExcerptHash)
                return new(null, TimeSpan.Zero, Unchanged: true);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new ConversationTitleRequest(text),
                    ConversationTitleJsonContext.Default.ConversationTitleRequest)
            };
            request.Headers.Add("X-Api-Key", settings.ApiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (settings != readSettings()) return new(null, TimeSpan.FromMinutes(1));
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                    return new(null, TimeSpan.FromHours(1), KeyWide: true);
                if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    var delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.FromMinutes(1);
                    return new(null, TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 30, 86400)), KeyWide: true);
                }
                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                    return new(null, TimeSpan.FromMinutes(5), KeyWide: true);
                return new(null, TimeSpan.FromMinutes(5), (int)response.StatusCode is >= 300 and < 500);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return new(null, TimeSpan.FromMinutes(5));
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[MaxResponseBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxResponseBytes) return new(null, TimeSpan.FromMinutes(5));
            var result = JsonSerializer.Deserialize(bytes.AsSpan(0, length),
                ConversationTitleJsonContext.Default.ConversationTitleResponse);
            var title = result?.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 160
                || title.Any(c => char.IsControl(c) || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')
                || CredentialRedactor.RedactTranscript(title, settings.ApiKey) != title
                || settings != readSettings())
                return new(null, TimeSpan.FromMinutes(5));
            return new(title, TimeSpan.Zero, ExcerptHash: excerptHash);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException)
        {
            // The endpoint is unreachable for every conversation, not just this one.
            return new(null, TimeSpan.FromMinutes(5), KeyWide: true);
        }
        catch (Exception)
        {
            // Includes timeouts, malformed JSON/headers and stream failures.
            // Exception messages may contain supplied data: keep them out of every log sink.
            return new(null, TimeSpan.FromMinutes(5));
        }
        finally
        {
            await monitorStop.CancelAsync();
            await monitor;
        }
    }

    /// <summary>The first <see cref="MaxExcerptChars"/> characters, never ending inside a surrogate pair.</summary>
    internal static string Excerpt(string redacted)
    {
        if (redacted.Length <= MaxExcerptChars)
            return redacted;
        var end = char.IsHighSurrogate(redacted[MaxExcerptChars - 1]) ? MaxExcerptChars - 1 : MaxExcerptChars;
        return redacted[..end];
    }

    private async Task WatchSettingsAsync(ConversationTitleSettings expected,
        CancellationTokenSource request, CancellationToken stop)
    {
        try
        {
            // Once a second is enough to honor a settings toggle without re-parsing the file constantly.
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop);
                ConversationTitleSettings current;
                try { current = readSettings(); }
                catch (Exception) { continue; } // Another root mid-save; the next read decides.
                if (current == expected) continue;
                await request.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}

internal sealed record ConversationTitleRequest(string Text);
internal sealed record ConversationTitleResponse(string? Title);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 8)]
[JsonSerializable(typeof(ConversationTitleRequest))]
[JsonSerializable(typeof(ConversationTitleResponse))]
[JsonSerializable(typeof(ConversationTitleChanged))]
internal partial class ConversationTitleJsonContext : JsonSerializerContext;
