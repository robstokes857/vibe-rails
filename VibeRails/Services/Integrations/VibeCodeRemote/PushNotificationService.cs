using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Forwards per-tab "tab is ready / waiting" push notifications to the VibeRails-Front
/// web-push API (<c>POST {frontendUrl}/api/v1/push/notify</c>). Reuses the same cloud
/// API key + frontend URL as the other remote integrations. Fire-and-forget: failures
/// (and a missing API key / URL) never disrupt the terminal UI.
/// </summary>
public interface IPushNotificationService
{
    Task SendAsync(string title, string body, string? tag, string? imageBase64, CancellationToken cancellationToken = default);
}

// AOT-compatible JSON serialization context. CamelCase matches the Front's web-default
// (case-insensitive) model binding.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PushNotifyRequest))]
internal partial class PushNotifyJsonContext : JsonSerializerContext
{
}

public record PushNotifyRequest(
    string Title,
    string Body,
    string Url,
    string? Tag,
    bool Renotify,
    string? ImageBase64
);

public class PushNotificationService : IPushNotificationService
{
    private readonly HttpClient _httpClient;

    // Light pre-check so we don't ship a giant payload to the cloud only to have it
    // rejected. The Front is the hard size gate; this just trims obvious overflow.
    // ~256 KB decoded ≈ ⌈bytes/3⌉·4 base64 chars.
    private const int MaxImageBase64Chars = 360_000;

    public PushNotificationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task SendAsync(string title, string body, string? tag, string? imageBase64, CancellationToken cancellationToken = default)
    {
        var apiKey = ParserConfigs.GetApiKey();
        var frontendUrl = ParserConfigs.GetFrontendUrl();

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(frontendUrl))
            return;

        // Drop an oversize screenshot rather than fail the whole notification — the text
        // push is the important part.
        if (!string.IsNullOrEmpty(imageBase64) && imageBase64.Length > MaxImageBase64Chars)
            imageBase64 = null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{frontendUrl.TrimEnd('/')}/api/v1/push/notify");
        request.Headers.Add("X-Api-Key", apiKey);

        var payload = new PushNotifyRequest(
            Title: title,
            Body: body,
            Url: "/Terminals",
            Tag: string.IsNullOrWhiteSpace(tag) ? null : tag,
            // Replace (rather than stack) a previous notification for the same tab.
            Renotify: true,
            ImageBase64: imageBase64
        );

        var json = JsonSerializer.Serialize(payload, PushNotifyJsonContext.Default.PushNotifyRequest);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                Log.Warning("[Push] VibeRails-Front returned {StatusCode} for push notification", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Push] Failed to send push notification");
        }
    }
}
