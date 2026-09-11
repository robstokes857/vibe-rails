using VibeRails.Services.Terminal;

namespace VibeRails.Services.Board;

/// <summary>Root backend: a linked session is "live" when one of the in-memory terminal tabs is running it.</summary>
public sealed class TerminalTabLiveSessionProbe(ITerminalTabHostService tabHost) : IBoardLiveSessionProbe
{
    public async Task<IReadOnlyDictionary<string, string>> GetLiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tab in await tabHost.ListTabsAsync(cancellationToken))
        {
            if (tab.HasActiveSession && !string.IsNullOrWhiteSpace(tab.SessionId))
                live[tab.SessionId!] = tab.TabId;
        }
        return live;
    }
}
