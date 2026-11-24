namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Centralizes all file type detection logic
    /// </summary>
    public class FileClassifier : IFileClassifier
    {
        private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".ts", ".py", ".java"
        };

        private static readonly HashSet<string> ComplexityCheckableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".ts"
        };

        private static readonly HashSet<string> PackageFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Node.js / JavaScript
            "package.json",
            "package-lock.json",
            "yarn.lock",
            "pnpm-lock.yaml",
            // Python
            "requirements.txt",
            "Pipfile",
            "Pipfile.lock",
            "pyproject.toml",
            "poetry.lock",
            "setup.py",
            // .NET
            "packages.config",
            "Directory.Packages.props",
            // Java / Kotlin
            "pom.xml",
            "build.gradle",
            "build.gradle.kts",
            "settings.gradle",
            "settings.gradle.kts",
            // Ruby
            "Gemfile",
            "Gemfile.lock",
            // Rust
            "Cargo.toml",
            "Cargo.lock",
            // Go
            "go.mod",
            "go.sum",
            // PHP
            "composer.json",
            "composer.lock"
        };

        private static readonly HashSet<string> PackageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj",
            ".fsproj",
            ".vbproj"
        };

        public bool IsCodeFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return CodeExtensions.Contains(ext);
        }

        /// <summary>
        /// A test is named by a convention with a boundary (MyTest.cs, parser_test.go, calc.spec.ts,
        /// test_math.py, a tests directory), never by merely containing the letters: treating
        /// Contest.cs or LatestReport.cs as tests silently exempts them from the coverage gate.
        /// The convention lives in MintLint, which also uses it to leave test files out of the
        /// measurements that do not apply to tests.
        /// </summary>
        public bool IsTestFile(string filePath) => MintLint.TestFileConvention.IsTestFile(filePath);

        public bool IsPackageFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            if (PackageFileNames.Contains(fileName))
                return true;

            var ext = Path.GetExtension(filePath);
            return PackageFileExtensions.Contains(ext);
        }

        public bool IsComplexityCheckable(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return ComplexityCheckableExtensions.Contains(ext);
        }
    }
}
