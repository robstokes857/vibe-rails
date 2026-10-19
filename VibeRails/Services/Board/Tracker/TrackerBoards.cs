namespace VibeRails.Services.Board.Tracker;

/// <summary>The wire names of the trackers a board or card can be linked to.</summary>
public static class TrackerProviders
{
    public const string Jira = "jira";
    public const string Ado = "ado";
}

/// <summary>
/// Which tracker a board is the dedicated board of. Only a dedicated connection makes one: a
/// legacy connection whose board is still the source board leaves it a local board. A dedicated
/// tracker board uses its tracker for remote access and never syncs with viberails.ai.
/// </summary>
public static class TrackerBoards
{
    /// <summary><see cref="TrackerProviders.Jira"/>, <see cref="TrackerProviders.Ado"/>, or null for a local board.</summary>
    public static async Task<string?> DedicatedProviderAsync(
        this IBoardStore store, string projectPath, string boardId, CancellationToken cancellationToken)
    {
        if (await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken) is { DedicatedBoard: true })
            return TrackerProviders.Jira;
        if (await store.GetAdoConnectionAsync(projectPath, boardId, cancellationToken) is { DedicatedBoard: true })
            return TrackerProviders.Ado;
        return null;
    }

    /// <summary>Board id to provider for every dedicated tracker board on this machine.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> DedicatedProvidersAsync(
        this IBoardStore store, CancellationToken cancellationToken)
    {
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var connection in await store.GetAdoConnectionsAsync(cancellationToken))
        {
            if (connection.DedicatedBoard)
                providers[connection.BoardId] = TrackerProviders.Ado;
        }
        // Jira is written last so it wins, as it does in DedicatedProviderAsync. A board has one of each at most.
        foreach (var connection in await store.GetJiraConnectionsAsync(cancellationToken))
        {
            if (connection.DedicatedBoard)
                providers[connection.BoardId] = TrackerProviders.Jira;
        }
        return providers;
    }

    /// <summary>The product name shown in messages.</summary>
    public static string DisplayName(string? provider) => provider == TrackerProviders.Ado ? "Azure DevOps" : "Jira";
}
