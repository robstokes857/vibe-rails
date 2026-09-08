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
                .Setup(x => x.GetDocumentedFilesAsync("vc.rules.md", It.IsAny<CancellationToken>()))
                .ReturnsAsync(documentedFiles);

            _mockPathNormalizer
                .Setup(x => x.Normalize(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((path, root) => path);

            var rule = new RuleWithEnforcement("Log all file changes", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("src/MyClass.cs", rule, "vc.rules.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ValidateAsync_WhenFileNotDocumented_ShouldFail()
        {
            // Arrange
            var documentedFiles = new List<string> { "src/MyClass.cs" };

            _mockAgentFileService
                .Setup(x => x.GetDocumentedFilesAsync("vc.rules.md", It.IsAny<CancellationToken>()))
                .ReturnsAsync(documentedFiles);

            _mockPathNormalizer
                .Setup(x => x.Normalize(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((path, root) => path);

            var rule = new RuleWithEnforcement("Log all file changes", Enforcement.COMMIT);

            // Act
            var result = await _validator.ValidateAsync("src/UndocumentedFile.cs", rule, "vc.rules.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("not documented", result.Message);
            Assert.Contains("vc.rules.md", result.Message);
        }

        [Fact]
        public async Task ValidateAsync_ShouldBeCaseInsensitive()
        {
            // Arrange
            var documentedFiles = new List<string> { "src/myclass.cs" };

            _mockAgentFileService
                .Setup(x => x.GetDocumentedFilesAsync("vc.rules.md", It.IsAny<CancellationToken>()))
                .ReturnsAsync(documentedFiles);

            _mockPathNormalizer
                .Setup(x => x.Normalize(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((path, root) => path.ToLowerInvariant());

            var rule = new RuleWithEnforcement("Log all file changes", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("src/MyClass.cs", rule, "vc.rules.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void SupportedRule_ShouldReturnLogAllFileChanges()
        {
            // Assert
            Assert.Equal(Rule.LogAllFileChanges, _validator.SupportedRule);
        }

        [Theory]
        [InlineData("nested/vc.rules.md", true)]
        [InlineData("./nested/vc.rules.md", true)]
        [InlineData("nested\\vc.rules.md", true)]
        [InlineData("nested/child/vc.rules.md", false)]
        [InlineData("nested/vc.rules.md.backup", false)]
        public async Task ValidateAsync_ExemptsOnlyTheDeclaringPolicy(string changedPath, bool expectedPass)
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vca-legacy-documentation"));
            var source = Path.Combine(root, "nested", "vc.rules.md");
            _mockAgentFileService.Setup(x => x.GetDocumentedFilesAsync(source, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var validator = new LogAllFileChangesValidator(_mockAgentFileService.Object, new PathNormalizer());

            var result = await validator.ValidateAsync(changedPath,
                new RuleWithEnforcement("Log all file changes", Enforcement.STOP), source, root,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(expectedPass, result.IsValid);
        }

        [Theory]
        [InlineData(5, Rule.LogFileChangesOver5Lines)]
        [InlineData(10, Rule.LogFileChangesOver10Lines)]
        public async Task ChangedLinesValidator_ExemptsOnlyTheDeclaringPolicy(int threshold, Rule supportedRule)
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vca-legacy-documentation"));
            var source = Path.Combine(root, "vc.rules.md");
            var reader = new Mock<IFileReader>();
            reader.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            reader.Setup(x => x.GetLineCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(20);
            var validator = new FileChangesOverLinesValidator(reader.Object, threshold, supportedRule);
            var rule = new RuleWithEnforcement($"Log file changes > {threshold} lines", Enforcement.STOP);

            var ownResult = await validator.ValidateAsync("vc.rules.md", rule, source, root,
                ct: TestContext.Current.CancellationToken);
            var nestedResult = await validator.ValidateAsync("nested/vc.rules.md", rule, source, root,
                ct: TestContext.Current.CancellationToken);

            Assert.True(ownResult.IsValid);
            Assert.False(nestedResult.IsValid);
        }
    }
}
