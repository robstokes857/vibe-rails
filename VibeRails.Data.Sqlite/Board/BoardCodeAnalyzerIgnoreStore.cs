using VibeRails.DB;

namespace VibeRails.Services.Board;

/// <summary>Routes current Code quality exemptions through the backed-up Board store.</summary>
public sealed class BoardCodeAnalyzerIgnoreStore(IBoardStore store) : ICodeAnalyzerIgnoreStore
{
    public Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> ListAsync(string repositoryPath, CancellationToken cancellationToken) =>
        store.GetCodeQualityIgnoresAsync(repositoryPath, cancellationToken);

    public Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> GetIgnoreRulesAsync(string repositoryPath, CancellationToken cancellationToken) =>
        ListAsync(repositoryPath, cancellationToken);

    public Task UpsertAsync(string repositoryPath, CodeAnalyzerIgnoredFile file, CancellationToken cancellationToken) =>
        UpsertManyAsync(repositoryPath, [file], cancellationToken);

    public Task UpsertManyAsync(string repositoryPath, IReadOnlyList<CodeAnalyzerIgnoredFile> files, CancellationToken cancellationToken) =>
        store.SaveCodeQualityIgnoresAsync(repositoryPath,
            files.Select(file => file with { Author = file.Author ?? Environment.UserName }).ToList(), cancellationToken);

    public Task<bool> RemoveAsync(string repositoryPath, string path, CancellationToken cancellationToken) =>
        store.RemoveCodeQualityIgnoreAsync(repositoryPath, path, Environment.UserName, cancellationToken);
}
