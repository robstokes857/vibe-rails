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
            var originalName = Path.GetFileNameWithoutExtension(filePath);
            var name = Path.GetFileName(filePath).ToLowerInvariant();
            var nameWithoutExt = Path.GetFileNameWithoutExtension(name);

            // Suffix conventions (MyTest.cs, MyTests.cs, calc.spec.ts, my.test.js) plus the
            // pytest and Java-style prefixes (test_math.py, TestOrders.java). A name that merely
            // contains the letters (Testimony.cs, LatestReport.cs, Inspector.cs) is production
            // code: treating it as a test silently exempts it from the coverage gate.
            if (nameWithoutExt.EndsWith("test") || nameWithoutExt.EndsWith("tests") ||
                nameWithoutExt.EndsWith("spec") || nameWithoutExt.EndsWith("specs") ||
                name.Contains(".test.") || name.Contains(".spec.") ||
                nameWithoutExt.StartsWith("test_") || nameWithoutExt.StartsWith("tests_") ||
                (originalName.StartsWith("Test", StringComparison.Ordinal)
                    && originalName.Length > 4
                    && char.IsUpper(originalName[4])))
                return true;

            // Test directories anywhere on the path, whichever separator the caller used. The
            // leading slash lets a repository-relative path that starts with the directory
            // (tests/helpers.py, spec/models/user.rb) match too.
            var normalized = "/" + filePath.Replace('\\', '/');
            return normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("/__tests__/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("/spec/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("/specs/", StringComparison.OrdinalIgnoreCase);
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
