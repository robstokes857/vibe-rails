using System.Net.Http.Json;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services;

public class SwarmService(HttpClient httpClient)
{
    public async Task<string> CreatePlanAsync(SwarmPlanRequest request, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/swarm")
        {
            Content = JsonContent.Create(request, AppJsonSerializerContext.Default.SwarmPlanRequest)
        };
        req.Headers.Add("X-Api-Key", ParserConfigs.GetApiKey());

        var response = await httpClient.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        var wrapper = await response.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.SwarmPlanWrapper, ct);

        return wrapper?.Plan ?? throw new InvalidOperationException("Swarm API returned no plan");
    }
}
