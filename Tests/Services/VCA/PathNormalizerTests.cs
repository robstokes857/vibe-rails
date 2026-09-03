using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA
{
    public class PathNormalizerTests
    {
        // GetScopedFiles runs the rule-file and root paths through Path.GetFullPath, so the
        // fake repo has to be an absolute path native to the OS running the tests. A hardcoded
        // "c:\repo" is relative on Linux (backslash is an ordinary filename character there),
        // which made these three tests return zero files on the Linux CI runner.
        private static readonly string RepoRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "viberails-path-normalizer-tests", "repo"));

        private readonly PathNormalizer _normalizer;

        public PathNormalizerTests()
        {
            _normalizer = new PathNormalizer();
        }

        [Theory]
        [InlineData("./src/file.cs", "src/file.cs")]
        [InlineData(".\\src\\file.cs", "src/file.cs")]
        [InlineData("src\\file.cs", "src/file.cs")]
        [InlineData("src/file.cs", "src/file.cs")]
        [InlineData("./file.cs", "file.cs")]
        [InlineData(".\\file.cs", "file.cs")]
        public void Normalize_ShouldNormalizePaths(string input, string expected)
        {
            // Act
            var result = _normalizer.Normalize(input, "/root");

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetScopedFiles_WhenAgentAtRoot_ShouldReturnAllFiles()
        {
            // Arrange
            var files = new List<string> { "src/file1.cs", "lib/file2.cs", "file3.cs" };
            var sourceFile = Path.Combine(RepoRoot, "vc.rules.md");
            var rootPath = RepoRoot;

            // Act
            var result = _normalizer.GetScopedFiles(files, sourceFile, rootPath);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.Contains("src/file1.cs", result);
            Assert.Contains("lib/file2.cs", result);
            Assert.Contains("file3.cs", result);
        }

        [Fact]
        public void GetScopedFiles_WhenAgentInSubdir_ShouldReturnOnlyScopedFiles()
        {
            // Arrange
            var files = new List<string> { "src/file1.cs", "lib/file2.cs", "src/sub/file3.cs" };
            var sourceFile = Path.Combine(RepoRoot, "src", "vc.rules.md");
            var rootPath = RepoRoot;

            // Act
            var result = _normalizer.GetScopedFiles(files, sourceFile, rootPath);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Contains("src/file1.cs", result);
            Assert.Contains("src/sub/file3.cs", result);
            Assert.DoesNotContain("lib/file2.cs", result);
        }

        [Fact]
        public void GetScopedFiles_ShouldBeCaseInsensitive()
        {
            // Arrange
            var files = new List<string> { "Src/File1.cs", "SRC/file2.cs" };
            var sourceFile = Path.Combine(RepoRoot, "src", "vc.rules.md");
            var rootPath = RepoRoot;

            // Act
            var result = _normalizer.GetScopedFiles(files, sourceFile, rootPath);

            // Assert
            Assert.Equal(2, result.Count);
        }
    }
}
