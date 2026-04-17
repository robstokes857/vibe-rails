namespace VibeRails.Services.Terminal;

public sealed class SessionActivityState : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public SessionActivityState(DateTimeOffset now, string cli)
    {
        LastInputUtc = now;
        LastOutputUtc = now;
        LastActivityUtc = now;
        Cli = cli;
    }

    public string Cli { get; }
    public DateTimeOffset LastInputUtc { get; set; }
    public DateTimeOffset LastOutputUtc { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }
    public DateTimeOffset LastWaitingForUserUtc { get; set; }
    public DateTimeOffset LastWorkingSeenUtc { get; set; }
    public bool IdleNotified { get; set; }
    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
