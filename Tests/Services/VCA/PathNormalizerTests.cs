using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA
{
    public class PathNormalizerTests
    {
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
            var sourceFile = "c:\\repo\\AGENTS.md";
            var rootPath = "c:\\repo";

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
            var sourceFile = "c:\\repo\\src\\AGENTS.md";
            var rootPath = "c:\\repo";

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
            var sourceFile = "c:\\repo\\src\\AGENTS.md";
            var rootPath = "c:\\repo";

            // Act
            var result = _normalizer.GetScopedFiles(files, sourceFile, rootPath);

            // Assert
            Assert.Equal(2, result.Count);
        }
    }
}
