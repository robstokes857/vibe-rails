namespace VibeRails.Services.Board.Sync;

/// <summary>Session and backup upload clients share the persisted account hold with Board sync.</summary>
public sealed class RemoteSafetyUploadHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Delete
            && request.Headers.TryGetValues("X-Api-Key", out var values) && values.SingleOrDefault() is { } key
            && request.RequestUri is { Scheme: "https", Host: "viberails.ai" } target)
        {
            var safety = new RemoteSafetyClient(() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }),
                () => target, () => key, "/api/v1/data-exports/remote-access");
            var generation = await safety.EnsureAsync(false, true, ct);
            request.Headers.TryAddWithoutValidation("X-VibeRails-Safety-Protocol", "1");
            request.Headers.TryAddWithoutValidation("X-VibeRails-Safety-Generation", generation);
        }
        return await base.SendAsync(request, ct);
    }
}
