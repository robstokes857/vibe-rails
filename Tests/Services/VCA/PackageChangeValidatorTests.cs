using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA
{
    public class PackageChangeValidatorTests
    {
        private readonly Mock<IFileClassifier> _mockFileClassifier;
        private readonly PackageChangeValidator _validator;

        public PackageChangeValidatorTests()
        {
            _mockFileClassifier = new Mock<IFileClassifier>();
            _validator = new PackageChangeValidator(_mockFileClassifier.Object);
        }

        [Fact]
        public async Task ValidateAsync_WhenNotPackageFile_ShouldPass()
        {
            // Arrange
            _mockFileClassifier.Setup(x => x.IsPackageFile("MyClass.cs"))
                .Returns(false);

            var rule = new RuleWithEnforcement("Package file changes", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("MyClass.cs", rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ValidateAsync_WhenPackageFile_ShouldFail()
        {
            // Arrange
            _mockFileClassifier.Setup(x => x.IsPackageFile("package.json"))
                .Returns(true);

            var rule = new RuleWithEnforcement("Package file changes", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("package.json", rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("Package file changed", result.Message);
            Assert.Contains("package.json", result.Message);
        }

        [Theory]
        [InlineData("package.json")]
        [InlineData("requirements.txt")]
        [InlineData("MyProject.csproj")]
        [InlineData("pom.xml")]
        public async Task ValidateAsync_WithVariousPackageFiles_ShouldFail(string fileName)
        {
            // Arrange
            _mockFileClassifier.Setup(x => x.IsPackageFile(fileName))
                .Returns(true);

            var rule = new RuleWithEnforcement("Package file changes", Enforcement.STOP);

            // Act
            var result = await _validator.ValidateAsync(fileName, rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
        }

        [Fact]
        public void SupportedRule_ShouldReturnPackageChangeDetected()
        {
            // Assert
            Assert.Equal(Rule.PackageChangeDetected, _validator.SupportedRule);
        }
    }
}
