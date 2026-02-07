namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Abstracts file I/O operations for validation
    /// </summary>
    public interface IFileReader
    {
        /// <summary>
        /// Checks if a file exists
        /// </summary>
        Task<bool> ExistsAsync(string filePath, CancellationToken ct);

        /// <summary>
        /// Gets the number of lines in a file
        /// </summary>
        Task<int> GetLineCountAsync(string filePath, CancellationToken ct);

        /// <summary>
        /// Reads all text content from a file
        /// </summary>
        Task<string> ReadAllTextAsync(string filePath, CancellationToken ct);
    }
}
