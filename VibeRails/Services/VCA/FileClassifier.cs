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

        private static readonly string[] TestStemSuffixes = ["Test", "Tests", "Spec", "Specs"];

        public bool IsTestFile(string filePath)
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            var lowerName = Path.GetFileName(filePath).ToLowerInvariant();
            var lowerStem = stem.ToLowerInvariant();

            // A test is named by a convention with a boundary, never by merely containing the
            // letters: Contest.cs, Testimony.cs, LatestReport.cs and Inspector.cs are production
            // code, and treating them as tests silently exempts them from the coverage gate.
            //   capitalized suffix   MyTest.cs, OrderTests.cs, ParserSpec.scala, ABTest.cs
            //   separator suffix     parser_test.go, user_spec.rb, calc-test.js
            //   dotted segment       my.test.js, calc.spec.ts
            //   prefix               test_math.py, TestOrders.java
            //   whole stem           test.py, tests.cs, spec.rb
            if (TestStemSuffixes.Any(suffix => HasCapitalizedSuffix(stem, suffix)) ||
                TestStemSuffixes.Any(suffix => HasSeparatedSuffix(lowerStem, suffix.ToLowerInvariant())) ||
                TestStemSuffixes.Any(suffix => lowerStem == suffix.ToLowerInvariant()) ||
                lowerName.Contains(".test.") || lowerName.Contains(".spec.") ||
                lowerStem.StartsWith("test_") || lowerStem.StartsWith("tests_") ||
                (stem.StartsWith("Test", StringComparison.Ordinal)
                    && stem.Length > 4
                    && char.IsUpper(stem[4])))
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

        // "MyTest" and "ABTest" end in a capitalized word; "Contest" and "Protest" do not.
        private static bool HasCapitalizedSuffix(string stem, string suffix) =>
            stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal);

        // "parser_test", "user_spec", "calc-test": the word is set off by a separator.
        private static bool HasSeparatedSuffix(string lowerStem, string lowerSuffix) =>
            lowerStem.EndsWith("_" + lowerSuffix, StringComparison.Ordinal)
            || lowerStem.EndsWith("-" + lowerSuffix, StringComparison.Ordinal);

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
