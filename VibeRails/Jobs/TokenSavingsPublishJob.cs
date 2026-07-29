using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Jobs;

/// <summary>
/// Periodically POSTs the cumulative lifetime tokens-saved total to the VibeRails endpoint so
/// aggregate savings can be reported across machines. The value is an absolute total (not a
/// delta); reposting upserts the record keyed by API key + computer name. A failed post never
/// crashes, slows, or spams the main app — <see cref="JobBase"/> isolates per-tick exceptions,
/// and this job additionally swallows non-success responses, unusable configuration, and every
/// transient request failure (including HttpClient timeouts, which arrive as
/// <see cref="TaskCanceledException"/> with the caller's token unsignalled).
/// </summary>
public sealed class TokenSavingsPublishJob(
    ILogger<TokenSavingsPublishJob> logger,
    ISystemResourceService resources,
    ITokenSavingsStore store,
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

    private const string DefaultEndpoint = "https://viberails.ai/api/v1/token-savings";
    private const int DefaultIntervalMinutes = 15;
    private const bool DefaultEnabled = true;

    private readonly bool _enabled = ReadEnabled(configuration);

    // Parsed once, at construction. Keeping this out of the tick means a malformed configured URL
    // can't throw UriFormatException on every tick forever. HTTPS is mandatory: the API key rides
    // in a custom X-Api-Key header, so an http:// endpoint would put the secret on the wire in
    // cleartext (the same guard DataExportService.TryGetExportUri applies).
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
            // and VS Code). Serialize the refresh + absolute upsert across all of them so a slow,
            // stale request can never arrive after and overwrite a newer total.
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

            // The savings that matter are recorded by terminal-tab children, never in this
            // process (see ITokenSavingsStore), so refresh the shared persisted total while the
            // cross-process lock is held before constructing the absolute upsert.
            await store.RefreshAsync();
            var total = store.GetTotals().TokensSaved;
            var computerName = ComputerNameFormatter.Machine();

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = JsonContent.Create(
                new TokenSavingsPostDto(computerName, total),
                AppJsonSerializerContext.Default.TokenSavingsPostDto);

            using var response = await httpClientFactory
                .CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning(
                    "[TokenSavingsPublishJob] Non-success status {Status} from {Endpoint}",
                    (int)response.StatusCode,
                    _endpoint);
                return;
            }

            Log.Information(
                "[TokenSavingsPublishJob] Published total={Total} computer={Computer}",
                total,
                computerName);
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
