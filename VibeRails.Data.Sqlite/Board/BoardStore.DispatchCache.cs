using System.Globalization;
using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const int WaitingDispatchCacheLimit = 4096;
    private static readonly TimeSpan WaitingDispatchRefresh = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly object _waitingDispatchLock = new();
    private readonly Dictionary<string, WaitingDispatchMemory> _waitingDispatches = [];
    private sealed record WaitingDispatchMemory(BoardLaneAutomationDispatch Dispatch, DateTime RecordedUtc, long AttemptUnixMs);

    private static string DispatchCacheKey(BoardLaneAutomationEvent entry) =>
        entry.EventKey + ":" + entry.JobId.ToString(CultureInfo.InvariantCulture);

    private bool RememberedWaitingDispatch(BoardLaneAutomationEvent entry, BoardLaneAutomationDispatch dispatch, DateTime now)
    {
        var key = DispatchCacheKey(entry);
        lock (_waitingDispatchLock)
        {
            if (dispatch.Status == "Waiting" && _waitingDispatches.TryGetValue(key, out var previous)
                && previous.Dispatch == dispatch && now >= previous.RecordedUtc && now - previous.RecordedUtc < WaitingDispatchRefresh)
            {
                _waitingDispatches[key] = previous with { AttemptUnixMs = new DateTimeOffset(now).ToUnixTimeMilliseconds() };
                return true;
            }
            _waitingDispatches.Remove(key);
            return false;
        }
    }

    private void RememberWaitingDispatch(BoardLaneAutomationEvent entry, BoardLaneAutomationDispatch dispatch, DateTime now)
    {
        if (dispatch.Status != "Waiting") return;
        lock (_waitingDispatchLock)
        {
            if (_waitingDispatches.Count >= WaitingDispatchCacheLimit)
                _waitingDispatches.Remove(_waitingDispatches.MinBy(p => p.Value.AttemptUnixMs).Key);
            _waitingDispatches[DispatchCacheKey(entry)] = new(dispatch, now, new DateTimeOffset(now).ToUnixTimeMilliseconds());
        }
    }

    // LastAttempt drives fairness across the 100-Job drain limit. Suppressed writes must still
    // rotate attempted Jobs in this process. Persist periodically for restarts/other roots;
    // never retain statuses here or use this cache to decide whether a step can run.
    private string WaitingDispatchAttemptsJson()
    {
        lock (_waitingDispatchLock)
            return JsonSerializer.Serialize(_waitingDispatches.ToDictionary(p => p.Key, p => p.Value.AttemptUnixMs),
                StorageJsonSerializerContext.Default.DictionaryStringInt64);
    }
}
