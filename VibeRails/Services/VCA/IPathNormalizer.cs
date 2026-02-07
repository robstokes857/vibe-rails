namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Handles path normalization and scoping logic for validation
    /// </summary>
    public interface IPathNormalizer
    {
        /// <summary>
        /// Normalizes a file path by removing leading prefixes and standardizing separators
        /// </summary>
        string Normalize(string path, string rootPath);

        /// <summary>
        /// Filters files to only those within the scope of the specified source file's directory
        /// </summary>
        List<string> GetScopedFiles(List<string> files, string sourceFile, string rootPath);
    }
}
