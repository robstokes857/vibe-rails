using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA
{
    public class LogAllFileChangesValidatorTests
    {
        private readonly Mock<IAgentFileService> _mockAgentFileService;
        private readonly Mock<IPathNormalizer> _mockPathNormalizer;
        private readonly LogAllFileChangesValidator _validator;

        public LogAllFileChangesValidatorTests()
        {
            _mockAgentFileService = new Mock<IAgentFileService>();
            _mockPathNormalizer = new Mock<IPathNormalizer>();
            _validator = new LogAllFileChangesValidator(
                _mockAgentFileService.Object,
                _mockPathNormalizer.Object);
        }

        [Fact]
        public async Task ValidateAsync_WhenFileDocumented_ShouldPass()
        {
            // Arrange
            var documentedFiles = new List<string> { "src/MyClass.cs", "src/MyOtherClass.cs" };

            _mockAgentFileService
                .Setup(x => x.GetDocumentedFilesAsync("AGENTS.md", It.IsAny<CancellationToken>()))
                .ReturnsAsync(documentedFiles);

            _mockPathNormalizer
                .Setup(x => x.Normalize(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((path, root) => path);

            var rule = new RuleWithEnforcement("Log all file changes", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("src/MyClass.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ValidateAsync_WhenFileNotDocumented_ShouldFail()
        {
            // Arrange
            var documentedFiles = new List<string> { "src/MyClass.cs" };

            _mockAgentFileService
                .Setup(x => x.GetDocumentedFilesAsync("AGENTS.md", It.IsAny<CancellationToken>()))
                .ReturnsAsync(documentedFiles);

            _mockPathNormalizer
                .Setup(x => x.Normalize(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((path, root) => path);

            var rule = new RuleWithEnforcement("Log all file changes", Enforcement.COMMIT);

            // Act
            var result = await _validator.ValidateAsync("src/UndocumentedFile.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("not documented", result.Message);
            Assert.Contains("AGENTS.md", result.Message);
        }

        [Fact]
        public async Task ValidateAsync_ShouldBeCaseInsensitive()
        {
            // Arrange
            var documentedFiles = new List<string> { "src/myclass.cs" };

            _mockAgentFileService
                .Setup(x => x.GetDocumentedFilesAsync("AGENTS.md", It.IsAny<CancellationToken>()))
                .ReturnsAsync(documentedFiles);

            _mockPathNormalizer
                .Setup(x => x.Normalize(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((path, root) => path.ToLowerInvariant());

            var rule = new RuleWithEnforcement("Log all file changes", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("src/MyClass.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void SupportedRule_ShouldReturnLogAllFileChanges()
        {
            // Assert
            Assert.Equal(Rule.LogAllFileChanges, _validator.SupportedRule);
        }
    }
}
