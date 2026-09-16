namespace VibeRails.DB;

/// <summary>Compatibility aggregate. New consumers should request the focused store they need.</summary>
public interface IRepository : ISessionStore, ISessionArchiveReader, IUserInputStore,
    IEnvironmentStore, ISandboxStore, IMetadataStore, IChatSummaryStore, IEmbeddingProgressStore
{
    void InitializeDatabase();
}
