namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Writes PTY output to the console. Used by the CLI terminal path.
/// Can be muted when a remote browser viewer takes over.
/// </summary>
public sealed class ConsoleOutputConsumer : ITerminalConsumer
{
    public bool Muted { get; set; }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (Muted) return;
        var text = System.Text.Encoding.UTF8.GetString(data.Span);
        Console.Write(text);
    }
}
