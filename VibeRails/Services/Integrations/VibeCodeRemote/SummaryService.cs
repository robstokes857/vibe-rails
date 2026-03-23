using VibeRails.DTOs;
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

            
            var apiKey = ParserConfigs.GetApiKey();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/summary")
            {
                Content = JsonContent.Create(new SummaryPostDto { SessionText = transcripts }, AppJsonSerializerContext.Default.SummaryPostDto)
            };
            request.Headers.Add("X-Api-Key", apiKey);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.SummaryResponseDto, cancellationToken);
            return result?.Summary ?? "";
        }
    }

}
