namespace VibeRails.Services.HttpRelay;

public interface IRemoteHttpRelayClient
{
    Task<HttpRelayResponse> SendAsync(
        HttpRelayRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately invalidates the current connection. The next request reconnects using the
    /// current frontend URL and API key.
    /// </summary>
    void Reset();
}
