using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Jobs;

/// <summary>
/// Periodically POSTs persisted increments to the VibeRails v2 endpoint. A durable checkpoint
/// and pending report keep key changes, renames and retries from replaying lifetime history.
/// A failed post never crashes, slows, or spams the main app — <see cref="JobBase"/> isolates per-tick exceptions,
/// and this job additionally swallows non-success responses, unusable configuration, and every
/// transient request failure (including HttpClient timeouts, which arrive as
/// <see cref="TaskCanceledException"/> with the caller's token unsignalled).
/// </summary>
public sealed class TokenSavingsPublishJob(
    ILogger<TokenSavingsPublishJob> logger,
    ISystemResourceService resources,
    ITokenSavingsPublishStore store,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : JobBase(logger, resources)
{
    /// <summary>
    /// Named client, deliberately not <c>AddHttpClient&lt;TokenSavingsPublishJob&gt;</c>: a typed
    /// registration is a *transient* that nothing resolves, while <c>AddHostedService&lt;T&gt;</c>
    /// activates its own singleton straight from the container. The hosted instance would then
    /// receive the default unnamed client (100s timeout, redirects on), not the configured one.
    /// </summary>
    internal const string HttpClientName = "token-savings";
    internal const string LockFileName = ".token-savings-publish.lock";

    private const string DefaultEndpoint = "https://viberails.ai/api/v2/token-savings";
    private const int DefaultIntervalMinutes = 15;
    private const bool DefaultEnabled = true;

    private readonly bool _enabled = ReadEnabled(configuration);

    // Parsed once, at construction. Keeping this out of the tick means a malformed configured URL
    // can't throw UriFormatException on every tick forever. HTTPS is mandatory: the API key rides
    // in a custom X-Api-Key header, so an http:// endpoint would put the secret on the wire in
    // cleartext. The session-data transport enforces the same rule for its endpoint.
    private readonly Uri? _endpoint = ParseHttpsEndpoint(
        configuration["VibeRails:TokenSavingsPublish:EndpointUrl"] ?? DefaultEndpoint);

    private readonly TimeSpan _interval = ReadInterval(configuration);

    protected override TimeSpan Interval => _interval;
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        if (!_enabled)
            return;

        if (_endpoint is null)
        {
            Log.Debug(
                "[TokenSavingsPublishJob] No usable HTTPS endpoint configured; skipping tick.");
            return;
        }

        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Debug("[TokenSavingsPublishJob] No API key configured; skipping tick.");
            return;
        }

        try
        {
            // More than one supported root backend can run at once (for example the browser app
            // and VS Code). Serialize preparing, sending and acknowledging one pending report.
            using var publishLock = CrossProcessFileLock.TryAcquire(
                CrossProcessFileLock.BesideStateDatabase(
                    ParserConfigs.GetStatePath(),
                    LockFileName));
            if (publishLock is null)
            {
                Log.Debug(
                    "[TokenSavingsPublishJob] Another process is publishing token savings; skipping tick.");
                return;
            }

            // The store reads the shared persisted tally, not this process's in-memory total.
            var report = await store.PrepareAsync(ComputerNameFormatter.Machine(), cancellationToken);
            if (report is null)
                return;

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = JsonContent.Create(
                new TokenSavingsIncrementDto(report.ReportId, report.ComputerId, report.ComputerName, report.TokensSaved),
                AppJsonSerializerContext.Default.TokenSavingsIncrementDto);

            var client = httpClientFactory.CreateClient(HttpClientName);
            // ResponseHeadersRead ends HttpClient's timeout at the headers. Bound the entire
            // exchange, including the receipt body, so a stalled response releases the lock.
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(client.Timeout);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                Log.Warning(
                    "[TokenSavingsPublishJob] Non-success status {Status} from {Endpoint}",
                    (int)response.StatusCode,
                    _endpoint);
                return;
            }

            var receipt = await ReadReceiptAsync(response.Content, requestTimeout.Token);
            if (receipt is null || receipt.ReportId != report.ReportId ||
                receipt.TokensSaved != report.TokensSaved || receipt.ReceivedUtc == default)
            {
                Log.Warning("[TokenSavingsPublishJob] Missing or mismatched receipt; retaining pending report.");
                return;
            }
            await store.AcknowledgeAsync(report.ReportId, cancellationToken);

            Log.Information(
                "[TokenSavingsPublishJob] Published increment={TokensSaved}",
                report.TokensSaved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // An HttpClient timeout is a TaskCanceledException with the caller's token *not*
            // signalled, so the filtered catch above rejects it. Without this second, unfiltered
            // clause the single most common transient failure would escape to JobBase and log a
            // full stack trace through both ILogger and Serilog on every tick, indefinitely.
            Log.Warning(
                "[TokenSavingsPublishJob] Request to {Endpoint} timed out; will retry next tick",
                _endpoint);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex,
                "[TokenSavingsPublishJob] Request failed to {Endpoint}; will retry next tick",
                _endpoint);
        }
        catch (Exception ex)
        {
            // Everything else — chiefly a header FormatException from an API key containing a
            // newline, which would otherwise repeat every tick. Log the exception *type* only:
            // the cause is user-supplied secret material and it must never reach the logs.
            Log.Warning(
                "[TokenSavingsPublishJob] Request to {Endpoint} failed ({ExceptionType}); will retry next tick",
                _endpoint,
                ex.GetType().Name);
        }
    }

    private static async Task<TokenSavingsReceiptDto?> ReadReceiptAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        // The response is a tiny receipt. Never buffer an unbounded error/proxy response.
        const int maximumBytes = 4096;
        if (content.Headers.ContentLength > maximumBytes)
            return null;
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
        }
        return length > maximumBytes ? null : JsonSerializer.Deserialize(
            buffer.AsSpan(0, length), AppJsonSerializerContext.Default.TokenSavingsReceiptDto);
    }

    private static Uri? ParseHttpsEndpoint(string? configuredValue) =>
        Uri.TryCreate(configuredValue, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? parsed
            : null;

    private static bool ReadEnabled(IConfiguration configuration)
    {
        const string key = "VibeRails:TokenSavingsPublish:Enabled";
        var configuredValue = configuration[key];
        if (configuredValue is null)
            return DefaultEnabled;

        if (bool.TryParse(configuredValue, out var enabled))
            return enabled;

        Log.Warning(
            "[TokenSavingsPublishJob] Invalid boolean setting {Setting}={Value}; using default {Default}",
            key,
            configuredValue,
            DefaultEnabled);
        return DefaultEnabled;
    }

    private static TimeSpan ReadInterval(IConfiguration configuration)
    {
        const string key = "VibeRails:TokenSavingsPublish:IntervalMinutes";
        var configuredValue = configuration[key];
        if (configuredValue is null)
            return TimeSpan.FromMinutes(DefaultIntervalMinutes);

        if (int.TryParse(configuredValue, out var minutes))
            return TimeSpan.FromMinutes(Math.Max(1, minutes));

        Log.Warning(
            "[TokenSavingsPublishJob] Invalid integer setting {Setting}={Value}; using default {Default}",
            key,
            configuredValue,
            DefaultIntervalMinutes);
        return TimeSpan.FromMinutes(DefaultIntervalMinutes);
    }
}
