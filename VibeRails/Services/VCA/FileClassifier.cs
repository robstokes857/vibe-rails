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

        public bool IsTestFile(string filePath)
        {
            var name = Path.GetFileName(filePath).ToLowerInvariant();
            var nameWithoutExt = Path.GetFileNameWithoutExtension(name);

            // Check if filename ends with "test" or "tests" (e.g., MyTest.cs, MyTests.cs)
            // or contains ".test." or ".spec." segments (e.g., my.test.js, my.spec.ts)
            if (nameWithoutExt.EndsWith("test") || nameWithoutExt.EndsWith("tests") ||
                nameWithoutExt.EndsWith("spec") || nameWithoutExt.EndsWith("specs") ||
                name.Contains(".test.") || name.Contains(".spec."))
                return true;

            // Check if file is in a test/tests directory
            return filePath.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                   filePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                   filePath.Contains("\\test\\", StringComparison.OrdinalIgnoreCase) ||
                   filePath.Contains("\\tests\\", StringComparison.OrdinalIgnoreCase);
        }

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
