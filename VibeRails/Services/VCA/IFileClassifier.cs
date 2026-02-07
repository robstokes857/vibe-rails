namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Classifies files by their type/extension for validation purposes
    /// </summary>
    public interface IFileClassifier
    {
        /// <summary>
        /// Checks if file is a code file (.cs, .js, .ts, .py, .java)
        /// </summary>
        bool IsCodeFile(string filePath);

        /// <summary>
        /// Checks if file is a test file (contains "test", "spec", or in test directory)
        /// </summary>
        bool IsTestFile(string filePath);

        /// <summary>
        /// Checks if file is a package file (package.json, *.csproj, etc.)
        /// </summary>
        bool IsPackageFile(string filePath);

        /// <summary>
        /// Checks if file supports cyclomatic complexity analysis (.cs, .js, .ts)
        /// </summary>
        bool IsComplexityCheckable(string filePath);
    }
}
