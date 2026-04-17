namespace VibeRails.Services.Terminal;

public interface ITerminalIoObserverService
{
    void Publish(TerminalIoEvent ioEvent);
    void PublishResize(TerminalResizeEvent resizeEvent);
    void PublishIdle(TerminalIdleEvent idleEvent);
    void PublishRemoteCommand(TerminalRemoteCommandEvent commandEvent);
    void PublishSessionStart(TerminalSessionStartEvent startEvent);
    void PublishSessionBusy(TerminalSessionBusyEvent busyEvent);
    void PublishWaitingForUser(TerminalWaitingForUserEvent waitingEvent);
    void PublishSessionComplete(TerminalSessionCompleteEvent completeEvent);
}
