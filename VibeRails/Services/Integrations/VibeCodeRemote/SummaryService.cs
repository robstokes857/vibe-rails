using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Security;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote
{

    public interface ISummaryService
    {
        Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken);
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

    public class SummaryService : ISummaryService
    {
        private readonly HttpClient _httpClient;
        public SummaryService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }
        public async Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(transcripts);

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

            var redactedTranscripts = CredentialRedactor.RedactTranscript(transcripts, apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/summary")
            {
                Content = JsonContent.Create(
                    new SummaryPostDto { SessionText = redactedTranscripts },
                    AppJsonSerializerContext.Default.SummaryPostDto)
            };
            request.Headers.Add("X-Api-Key", apiKey);

            // Every failure below becomes fixed text: an exception escaping the route would turn
            // into a header-stripped 500 that the browser reports as "cannot reach the host".
            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw RemoteSummaryException.ForUpstreamStatus((int)response.StatusCode);
                var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.SummaryResponseDto, cancellationToken);
                return result?.Summary ?? "";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new RemoteSummaryException(StatusCodes.Status504GatewayTimeout,
                    "viberails.ai took too long to generate the summary. Try again in a moment.");
            }
            catch (HttpRequestException)
            {
                throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                    "Could not reach viberails.ai to generate the summary. Check your connection and try again.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new RemoteSummaryException(StatusCodes.Status502BadGateway,
                    "viberails.ai returned a summary response VibeRails could not read.");
            }
        }
    }

}
