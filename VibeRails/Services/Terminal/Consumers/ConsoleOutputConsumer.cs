namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Writes PTY output to the console. Used by the CLI terminal path.
/// </summary>
public sealed class ConsoleOutputConsumer : ITerminalConsumer
{
    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data.Span);
        Console.Write(text);
    }
}
