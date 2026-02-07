namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Implements path normalization and scoping logic
    /// </summary>
    public class PathNormalizer : IPathNormalizer
    {
        public string Normalize(string path, string rootPath)
        {
            // Remove leading ./ or .\
            path = path.TrimStart('.').TrimStart('/', '\\');

            // Normalize separators
            path = path.Replace('\\', '/');

            return path;
        }

        public List<string> GetScopedFiles(List<string> files, string sourceFile, string rootPath)
        {
            var agentDir = Path.GetDirectoryName(Path.GetFullPath(sourceFile)) ?? "";
            var rootFull = Path.GetFullPath(rootPath);

            // Get relative path of the agent's directory from the repo root
            var relativeAgentDir = Path.GetRelativePath(rootFull, agentDir)
                .Replace('\\', '/');

            // If agent is at repo root, all files are in scope
            if (relativeAgentDir == ".")
                return files;

            // Ensure prefix ends with / for proper prefix matching
            var prefix = relativeAgentDir + "/";

            return files
                .Where(f => Normalize(f, rootPath)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
