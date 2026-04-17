namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// ITerminalConsumer that feeds all PTY output bytes into the headless
/// TerminalEmulator so GetGridReplay() always reflects the current screen state.
/// Thread-safe via the shared emulator lock.
/// </summary>
internal sealed class TerminalEmulatorConsumer : ITerminalConsumer
{
    private readonly TerminalEmulator.Terminal _emulator;
    private readonly Lock _lock;

    public TerminalEmulatorConsumer(TerminalEmulator.Terminal emulator, Lock emulatorLock)
    {
        _emulator = emulator;
        _lock = emulatorLock;
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        lock (_lock)
            _emulator.Write(data.Span);
    }
}
