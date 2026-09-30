namespace VibeRails.Data.Replay;

/// <summary>Read-only snapshots of existing local recordings. Never initializes or migrates storage.</summary>
public interface IReplayStore
{
    AppStatus Status();
    SessionPage Sessions(string? search, int offset);
    Manifest? Manifest(string id);
    FramePage Frames(string id, long after, long max, string source);
    ExchangePage Exchanges(string id, long after, long max);
    DiffDetail? Diff(string id, long changeId);
    ExchangeDetail? ExchangeDetail(string id, string exchangeId);
}
