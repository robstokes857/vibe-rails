namespace VibeRails.Data.Replay;

/// <summary>Read-only snapshots of existing local recordings. Never initializes or migrates storage.</summary>
public interface IReplayStore
{
    AppStatus Status();
    SessionPage Sessions(string? search, int offset);
    Manifest? Manifest(string id);
    FramePage Frames(string id, long after, long max, string source);
    /// <summary>The newest frames of a recording, up to about <paramref name="maxBytes"/>, oldest first.</summary>
    SessionTail Tail(string id, int maxBytes);
    ExchangePage Exchanges(string id, long after, long max);
    DiffDetail? Diff(string id, long changeId);
    ExchangeDetail? ExchangeDetail(string id, string exchangeId);
}
