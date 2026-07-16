namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Process-local token-saver gate. Every Web UI terminal tab runs in its own child VibeRails
/// process, so a singleton here is naturally scoped to exactly one tab without putting tab ids
/// into the TokenSaver library or the global settings file.
/// </summary>
public interface ITabTokenSaverState
{
    bool Enabled { get; set; }
}

public sealed class TabTokenSaverState : ITabTokenSaverState
{
    private int _enabled = 1;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }
}
