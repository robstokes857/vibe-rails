namespace VibeRails.Services;

/// <summary>
/// Fire-and-forget event bus. Publish type+text to all current WebSocket subscribers.
/// No buffer — if nobody is listening, the publish is a no-op.
/// tabId is null for main-process events; child-tab relay events carry the child's tabId.
/// </summary>
public sealed class EventBus
{
    public event Action<string, string, string?>? OnEvent;

    public void Publish(string type, string text, string? tabId = null)
    {
        OnEvent?.Invoke(type, text, tabId);
    }
}
