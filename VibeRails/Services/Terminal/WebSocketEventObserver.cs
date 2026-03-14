namespace VibeRails.Services.Terminal;

/// <summary>
/// Publishes all terminal I/O events to the EventBus so connected WebSocket viewers
/// at /tooling/events/terminal/ receive them in real time.
/// </summary>
public sealed class WebSocketEventObserver : ITerminalIoObserver
{
    private readonly EventBus _eventBus;

    public WebSocketEventObserver(EventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
    {
        // Never publish raw terminal input — it contains typed passwords and secrets.
        if (ioEvent.Direction == TerminalIoDirection.Input)
            return ValueTask.CompletedTask;

        _eventBus.Publish("terminal_output", ioEvent.Text);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalResizeAsync(TerminalResizeEvent resizeEvent, CancellationToken cancellationToken = default)
    {
        _eventBus.Publish("terminal_resize", $"{resizeEvent.Cols}x{resizeEvent.Rows}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
    {
        _eventBus.Publish("terminal_idle", $"idle for {idleEvent.IdleFor.TotalSeconds:F1}s");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalRemoteCommandAsync(TerminalRemoteCommandEvent commandEvent, CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrEmpty(commandEvent.Payload)
            ? commandEvent.Command
            : $"{commandEvent.Command}: {commandEvent.Payload}";
        _eventBus.Publish("terminal_remote_cmd", text);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnSessionStartAsync(TerminalSessionStartEvent startEvent, CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"cli      = {startEvent.Cli}");
        sb.Append($"\nworkDir  = {startEvent.WorkDir}");
        if (!string.IsNullOrEmpty(startEvent.EnvName))
            sb.Append($"\nenv      = {startEvent.EnvName}");
        foreach (var cmd in startEvent.SetupCommands)
            sb.Append($"\nsetup    = {cmd}");
        sb.Append($"\nlaunch   = {startEvent.LaunchCommand}");
        sb.Append($"\nsession  = {startEvent.SessionId}");
        _eventBus.Publish("terminal_session_start", sb.ToString());
        return ValueTask.CompletedTask;
    }
}
