using System.Net;
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
    bool KeyWide = false);

/// <summary>The Vibe Story (Board card) a conversation is attached to, sent as background for its title.</summary>
public sealed record ConversationTitleStory(string Id, string Title, string Description);

/// <summary>Sends the user's redacted messages, with any story as background, to the fixed VibeRails title API.</summary>
public sealed class ConversationTitleClient
{
    internal const string HttpClientName = "conversation-titles";
    internal const string Endpoint = "https://viberails.ai/api/v1/conversation-title";
    internal const int MaxResponseBytes = 8192;
    /// <summary>
    /// The user's own messages, not the screen transcript: launch prompts such as the story-tracking
    /// nudge are never captured as input, and agent output cannot crowd out what the user asked.
    /// With the story, the request stays under the API's 25,000-character limit.
    /// </summary>
    internal const int MaxUserTextChars = 20_000;
    internal const int MaxStoryTitleChars = 200;
    internal const int MaxStoryDescriptionChars = 1_500;
    internal const string StoryHeading =
        "Story information you might find useful (the Vibe Story this chat is attached to; other chats may share it):";
    internal const string UserTextHeading = "User text to make the title from:";
    /// <summary>The API's answer when nothing meaningful remains; never saved as a name.</summary>
    internal const string FallbackTitle = "New conversation";
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

    /// <summary>Reads current permission without loading or sending any conversation text.</summary>
    public ConversationTitleSettings CurrentSettings => readSettings();

    /// <summary>
    /// Requests the conversation's one title. No remote response/error text, credentials or user
    /// text are logged.
    /// </summary>
    public async Task<ConversationTitleResult> GenerateAsync(string userText,
        ConversationTitleSettings settings, CancellationToken ct, ConversationTitleStory? story = null)
    {
        if (!settings.CanSend || settings != readSettings())
            return new(null, TimeSpan.FromMinutes(1));
        if (string.IsNullOrWhiteSpace(userText))
            return new(null, TimeSpan.FromMinutes(1));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var monitorStop = new CancellationTokenSource();
        var monitor = WatchSettingsAsync(settings, deadline, monitorStop.Token);
        try
        {
            var text = ComposeRequest(userText, story, settings.ApiKey);
            if (settings != readSettings()) return new(null, TimeSpan.FromMinutes(1));
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
            // The API found no meaningful topic. The conversation's one request is spent either way.
            if (string.Equals(title, FallbackTitle, StringComparison.OrdinalIgnoreCase))
                return new(null, TimeSpan.Zero, Permanent: true);
            return new(title, TimeSpan.Zero);
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

    /// <summary>
    /// Frames the story as optional background and the user's messages as what the title names, so
    /// conversations sharing a story do not all receive its title. Each part is redacted whole before
    /// it is bounded, so a secret is never cut short of the context that lets the redactor see it.
    /// </summary>
    internal static string ComposeRequest(string userText, ConversationTitleStory? story, string apiKey)
    {
        var user = Bound(CredentialRedactor.RedactTranscript(userText.Trim(), apiKey), MaxUserTextChars);
        if (story is null)
            return $"{UserTextHeading}\n{user}";

        var title = Bound(CredentialRedactor.RedactTranscript(story.Title.Trim(), apiKey), MaxStoryTitleChars);
        var description = Bound(CredentialRedactor.RedactTranscript(story.Description.Trim(), apiKey), MaxStoryDescriptionChars);
        var builder = new StringBuilder(StoryHeading).Append('\n').Append(story.Id).Append(": ").Append(title);
        if (description.Length > 0)
            builder.Append('\n').Append(description);
        return builder.Append("\n\n").Append(UserTextHeading).Append('\n').Append(user).ToString();
    }

    /// <summary>
    /// At most <paramref name="max"/> characters, ending in "…" when cut: at the last word break in the
    /// second half if there is one, and never inside a surrogate pair.
    /// </summary>
    internal static string Bound(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var end = max - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        var space = text.LastIndexOfAny([' ', '\n', '\t'], end - 1, end - max / 2);
        if (space > 0) end = space;
        return text[..end].TrimEnd() + "…";
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
