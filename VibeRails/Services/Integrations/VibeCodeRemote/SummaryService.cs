using VibeRails.DTOs;
using VibeRails.Security;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote
{

    public interface ISummaryService
    {
        Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken);
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
                throw new InvalidOperationException(
                    "Remote summaries are disabled. Enable Remote Access before sending a session transcript.");
            }

            var apiKey = ParserConfigs.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
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
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.SummaryResponseDto, cancellationToken);
            return result?.Summary ?? "";
        }
    }

}
