using Pty.Net;

namespace VibeRails.Services.Terminal
{
    public class Terminal
    {
        private readonly IPtyConnection _ptyConnection;

        private async Task<string> ReadOutputAsync(CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(_ptyConnection.ReaderStream);
            var output = await reader.ReadToEndAsync(cancellationToken);
            return output;
        }
    }
}
