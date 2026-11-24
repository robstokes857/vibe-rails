using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Security;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote
{

    public interface ISummaryService
    {
        Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken);

        /// <summary>
        /// Send to (VIBE-363): redacts the text xterm.js rendered from a whole recording and asks
        /// viberails.ai (gpt-6.1-terra, high reasoning) for a handoff of at most
        /// <paramref name="maxChars"/> characters. <paramref name="sessionId"/> and
        /// <paramref name="projectName"/> (the project's display name, never its path) name the
        /// recorded session the text came from. Failures are <see cref="RemoteSummaryException"/>s.
        /// </summary>
        Task<string> GetHandoffAsync(string sessionId, string projectName, string renderedSession, int maxChars, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A summary could not be produced. <see cref="Exception.Message"/> is fixed local text written
    /// for the user (it never carries the remote body), and <see cref="StatusCode"/> is the status a
    /// local route should answer with. Upstream failures use 502/504, never 401: a local 401 would
    /// send the browser to sign in again.
    /// </summary>
    public sealed class RemoteSummaryException(int statusCode, string message, int? upstreamStatus = null)
        : Exception(message)
    {
        public int StatusCode { get; } = statusCode;

        /// <summary>The viberails.ai HTTP status, when it answered.</summary>
        public int? UpstreamStatus { get; } = upstreamStatus;

        internal static RemoteSummaryException ForUpstreamStatus(int status) => new(
            StatusCodes.Status502BadGateway,
            status switch
            {
                401 or 403 => "viberails.ai did not accept your VibeRails API key for summaries. Check that the key is current and has the Integrations permission.",
                413 => "This session is too large for viberails.ai to summarize.",
                429 => "viberails.ai is limiting summary requests right now. Try again in a minute.",
                >= 500 => $"viberails.ai could not generate the summary (HTTP {status}). Try again later.",
                _ => $"viberails.ai refused the summary request (HTTP {status})."
            },
            status);
    }

    /// <summary>The Send to handoff's limits, shared by the route, this client and the browser.</summary>
    public static class SessionHandoffLimits
    {
        /// <summary>Owner decision (2026-11-23, VIBE-363). viberails.ai refuses more with 413.</summary>
        public const int MaxInputTokens = 800_000;

        /// <summary>
        /// viberails.ai streams keep-alives for up to its 20-minute provider deadline; this leaves
        /// room for the upload and the answer.
        /// </summary>
        public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(25);

        /// <summary>
        /// Four UTF-8 bytes per token: viberails.ai and session-handoff-text.js use the same estimate.
        /// </summary>
        public static long EstimateTokens(string text) => ((long)System.Text.Encoding.UTF8.GetByteCount(text) + 3) / 4;
    }

    public class SummaryService : ISummaryService
    {
        /// <summary>The summary call's deadline: HttpClient's former default, now per call.</summary>
        internal static readonly TimeSpan SummaryDeadline = TimeSpan.FromSeconds(100);

        // The final event carries at most a 100,000-character handoff, JSON-escaped.
        private const int MaxHandoffLineChars = 1_048_576;

        private readonly HttpClient _httpClient;
        public SummaryService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(transcripts);
            var apiKey = RequireRemoteSummaries();

            var redactedTranscripts = CredentialRedactor.RedactTranscript(transcripts, apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/summary")
            {
                Content = JsonContent.Create(
                    new SummaryPostDto { SessionText = redactedTranscripts },
                    AppJsonSerializerContext.Default.SummaryPostDto)
            };
            request.Headers.Add("X-Api-Key", apiKey);

            return await SendAsync(SummaryDeadline, "viberails.ai took too long to generate the summary. Try again in a moment.",
                async token =>
                {
                    using var response = await _httpClient.SendAsync(request, token);
                    if (!response.IsSuccessStatusCode)
                        throw RemoteSummaryException.ForUpstreamStatus((int)response.StatusCode);
                    var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.SummaryResponseDto, token);
                    return result?.Summary ?? "";
                }, cancellationToken);
        }

        public async Task<string> GetHandoffAsync(string sessionId, string projectName, string renderedSession, int maxChars, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentNullException.ThrowIfNullOrWhiteSpace(renderedSession);
            var apiKey = RequireRemoteSummaries();

            var redacted = CredentialRedactor.RedactTranscript(renderedSession, apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/summary/handoff")
            {
                Content = JsonContent.Create(
                    new HandoffPostDto { SessionId = sessionId, ProjectName = projectName, SessionText = redacted, MaxChars = maxChars },
                    AppJsonSerializerContext.Default.HandoffPostDto)
            };
            request.Headers.Add("X-Api-Key", apiKey);

            return await SendAsync(SessionHandoffLimits.Deadline,
                "viberails.ai took longer than 25 minutes to write the handoff. Try again, or try a shorter session.",
                async token =>
                {
                    // Headers first: the body is an event stream that stays open while Terra works.
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode)
                        throw await HandoffRefusalAsync(response, token);
                    return await ReadHandoffStreamAsync(response, token);
                }, cancellationToken);
        }

        private static string RequireRemoteSummaries()
        {
            if (!ParserConfigs.GetRemoteAccess())
            {
                throw new RemoteSummaryException(StatusCodes.Status400BadRequest,
                    "Remote summaries are disabled. Enable Remote Access before sending a session transcript.");
            }

            var apiKey = ParserConfigs.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new RemoteSummaryException(StatusCodes.Status400BadRequest,
                    "Remote summaries require a configured VibeRails API key.");
            }
            return apiKey;
        }

        // Every failure below becomes fixed text: an exception escaping the route would turn
        // into a header-stripped 500 that the browser reports as "cannot reach the host".
        private static async Task<string> SendAsync(TimeSpan deadline, string timeoutMessage,
            Func<CancellationToken, Task<string>> send, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(deadline);
            try
            {
                return await send(timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new RemoteSummaryException(StatusCodes.Status504GatewayTimeout, timeoutMessage);
            }
            catch (HttpRequestException)
            {
                throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                    "Could not reach viberails.ai to generate the summary. Check your connection and try again.");
            }
            catch (IOException)
            {
                throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                    "The connection to viberails.ai closed before the handoff arrived. Try again.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                    "viberails.ai returned a summary response VibeRails could not read.");
            }
        }

        // Refusals before the stream starts. Only viberails.ai's own codes and counts are read.
        private static async Task<RemoteSummaryException> HandoffRefusalAsync(HttpResponseMessage response, CancellationToken token)
        {
            var status = (int)response.StatusCode;
            if (status == 404)
                return new RemoteSummaryException(StatusCodes.Status502BadGateway,
                    "viberails.ai does not support full-session handoffs yet. Try again after it updates.", status);
            HandoffRefusalDto? refusal = null;
            if (status is 413 or 429)
            {
                try
                {
                    refusal = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.HandoffRefusalDto, token);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    // Fall back to the status alone.
                }
            }
            return status switch
            {
                413 when refusal is { EstimatedTokens: > 0, MaxTokens: > 0 } => TooLarge(refusal.EstimatedTokens, refusal.MaxTokens, status),
                413 => new RemoteSummaryException(StatusCodes.Status413PayloadTooLarge,
                    "This session is too long for a handoff. viberails.ai accepts up to 800,000 tokens of session text.", status),
                429 when refusal?.Code == "handoff_busy" => new RemoteSummaryException(StatusCodes.Status429TooManyRequests,
                    "Another handoff for your account is still being written. Try again when it finishes.", status),
                _ => RemoteSummaryException.ForUpstreamStatus(status)
            };
        }

        internal static RemoteSummaryException TooLarge(long estimatedTokens, long maxTokens, int? upstreamStatus = null) => new(
            StatusCodes.Status413PayloadTooLarge,
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"This session is about {estimatedTokens:N0} tokens of text. Send to accepts up to {maxTokens:N0}, so nothing was sent to the model."),
            upstreamStatus);

        // ": ..." lines are keep-alives; the one "data: " line is the result or the error.
        private static async Task<string> ReadHandoffStreamAsync(HttpResponseMessage response, CancellationToken token)
        {
            await using var body = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(body);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.Length > MaxHandoffLineChars)
                    throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                        "viberails.ai returned a handoff larger than VibeRails accepts.");
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var outcome = JsonSerializer.Deserialize(line.AsSpan(5).TrimStart(), AppJsonSerializerContext.Default.HandoffStreamEventDto);
                if (outcome?.Type == "result" && !string.IsNullOrWhiteSpace(outcome.Summary))
                    return outcome.Summary;
                throw HandoffStreamFailure(outcome);
            }
            throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                "viberails.ai ended the handoff without a result. Try again.");
        }

        private static RemoteSummaryException HandoffStreamFailure(HandoffStreamEventDto? outcome) => outcome?.Code switch
        {
            "summary_timeout" => new(StatusCodes.Status504GatewayTimeout,
                "Terra took longer than 20 minutes to write the handoff. Try again, or try a shorter session.", outcome?.Status),
            "handoff_too_large" => new(StatusCodes.Status413PayloadTooLarge,
                "This session is longer than the model accepts, so no handoff was written.", outcome?.Status),
            "handoff_rate_limited" => new(StatusCodes.Status502BadGateway,
                "The model provider is limiting requests right now. Try again in a minute.", outcome?.Status),
            "summary_rejected" => new(StatusCodes.Status502BadGateway,
                "The model declined to write a handoff for this session.", outcome?.Status),
            _ when outcome?.Type == "result" => new(StatusCodes.Status502BadGateway,
                "viberails.ai returned an empty handoff. Try again.", outcome?.Status),
            _ => new(StatusCodes.Status502BadGateway,
                "viberails.ai could not write the handoff. Try again later.", outcome?.Status)
        };
    }

}
