namespace VibeRails.Services.Terminal;

public interface ISessionOutputWriter : IAsyncDisposable
{
    void Initialize(string sessionId, int cols = 120, int rows = 40);
    void Enqueue(byte[] payload);
    void NotifyResize(int cols, int rows);
}
