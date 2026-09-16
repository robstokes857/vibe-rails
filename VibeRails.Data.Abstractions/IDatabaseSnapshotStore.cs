namespace VibeRails.Data.Abstractions;

/// <summary>Creates a consistent offline database snapshot, including committed WAL contents.</summary>
public interface IDatabaseSnapshotStore
{
    /// <summary>Copies the source into an already secured, empty destination file.</summary>
    void CreateSnapshot(string sourcePath, string destinationPath);
}
