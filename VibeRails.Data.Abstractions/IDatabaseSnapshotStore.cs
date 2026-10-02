namespace VibeRails.Data.Abstractions;

/// <summary>Creates a consistent offline database snapshot, including committed WAL contents.</summary>
public interface IDatabaseSnapshotStore
{
    /// <summary>Copies the source into an already secured, empty destination file.</summary>
    void CreateSnapshot(string sourcePath, string destinationPath);

    /// <summary>Creates a cancellable snapshot without holding the caller's request thread.</summary>
    Task CreateSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); CreateSnapshot(sourcePath, destinationPath); }, cancellationToken);
}
