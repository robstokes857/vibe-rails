using VibeRails.DB;

namespace VibeRails.Services.Board;

/// <summary>An attributed file exemption; a null card id makes it repository-wide.</summary>
public sealed record CodeQualityExemption(string Id, string Path, string? CardId, string Reason,
    BoardAuthor Author, DateTime CreatedUtc)
{
    /// <summary>The exact rule captured with check evidence and consumed by the quality engines.</summary>
    public CodeAnalyzerIgnoredFile ToIgnoreRule() => new(Path, CodeAnalyzerIgnoreMatchKind.File,
        "exemption", Reason, CreatedUtc, Author.Label, Id, CardId);
}

public partial interface IBoardStore
{
    /// <summary>Lists active repository exemptions and, when supplied, this card's exemptions.</summary>
    Task<IReadOnlyList<CodeQualityExemption>> GetCodeQualityExemptionsAsync(string projectPath,
        string? cardId = null, CancellationToken cancellationToken = default);

    /// <summary>Saves an exemption and retains any superseded entry; validates the writable source card atomically.</summary>
    Task<CodeQualityExemption> SaveCodeQualityExemptionAsync(string projectPath, string cardId, string path,
        bool permanent, string reason, BoardAuthor author, CancellationToken cancellationToken = default);

    /// <summary>Revokes one exemption in this card's repository, retaining its original data and revocation author.</summary>
    Task<bool> RevokeCodeQualityExemptionAsync(string projectPath, string cardId, string exemptionId,
        BoardAuthor author, CancellationToken cancellationToken = default);
}
