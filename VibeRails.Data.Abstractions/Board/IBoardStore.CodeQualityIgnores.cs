using VibeRails.DB;

namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>Reads current exemptions from board.db. Older versions' state.db edits are adopted first, at most once per repository every five minutes per process.</summary>
    Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> GetCodeQualityIgnoresAsync(string repositoryPath, CancellationToken cancellationToken = default);
    /// <summary>Saves exemptions and their attributed history in one Board transaction.</summary>
    Task SaveCodeQualityIgnoresAsync(string repositoryPath, IReadOnlyList<CodeAnalyzerIgnoredFile> files, CancellationToken cancellationToken = default);
    /// <summary>Retires an exemption while retaining its text, attribution and removal history.</summary>
    Task<bool> RemoveCodeQualityIgnoreAsync(string repositoryPath, string path, string author, CancellationToken cancellationToken = default);
}
