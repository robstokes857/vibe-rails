using VibeRails.DB;
using VibeRails.Services.AgentTools;

namespace VibeRails.Services.Board;

/// <summary>Resolves the same explicitly scoped exemptions for lane checks, previews and Git Guard.</summary>
public sealed class CodeQualityExemptionService(IServiceProvider services)
{
    /// <summary>
    /// Explicit checks supply their project/card. Hooks use the inherited session only when the
    /// checked repository is that project's root or the session's recorded checkout. No card is
    /// inferred from a branch name, commit-message text or another active terminal.
    /// </summary>
    public async Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> GetRulesAsync(string repositoryPath,
        CancellationToken cancellationToken, string? projectPath = null, string? cardId = null)
    {
        // Lazy: merely constructing a hook must not open board.db or depend on its health.
        var store = services.GetRequiredService<IBoardStore>();
        var repository = BoardPaths.NormalizeProjectPath(repositoryPath);
        if (projectPath is null)
        {
            projectPath = repository;
            var session = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable)?.Trim();
            if (!string.IsNullOrEmpty(session) && await store.FindSessionLinkAsync(session, cancellationToken) is { } link
                && (string.Equals(repository, BoardPaths.NormalizeProjectPath(link.ProjectPath), BoardPaths.ProjectPathComparison)
                    || await store.IsSessionVisibleInProjectAsync(repository, session, cancellationToken)))
            {
                projectPath = link.ProjectPath;
                cardId = link.CardId;
            }
        }
        return (await store.GetCodeQualityExemptionsAsync(projectPath, cardId, cancellationToken))
            .Select(entry => entry.ToIgnoreRule()).ToList();
    }
}
