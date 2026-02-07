using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA
{
    public class CyclomaticComplexityValidatorTests
    {
        private readonly Mock<IFileReader> _mockFileReader;
        private readonly Mock<IFileClassifier> _mockFileClassifier;
        private readonly CyclomaticComplexityValidator _validator;

        public CyclomaticComplexityValidatorTests()
        {
            _mockFileReader = new Mock<IFileReader>();
            _mockFileClassifier = new Mock<IFileClassifier>();
            _validator = new CyclomaticComplexityValidator(
                _mockFileReader.Object,
                _mockFileClassifier.Object,
                20,
                Rule.CyclomaticComplexityUnder20);
        }

        [Fact]
        public async Task ValidateAsync_WhenFileDoesNotExist_ShouldPass()
        {
            // Arrange
            _mockFileReader.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var rule = new RuleWithEnforcement("Cyclomatic complexity < 20", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("test.cs", rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ValidateAsync_WhenFileNotComplexityCheckable_ShouldPass()
        {
            // Arrange
            _mockFileReader.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _mockFileClassifier.Setup(x => x.IsComplexityCheckable("test.py"))
                .Returns(false);

            var rule = new RuleWithEnforcement("Cyclomatic complexity < 20", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("test.py", rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ValidateAsync_WhenComplexityUnderThreshold_ShouldPass()
        {
            // Arrange
            var simpleCode = @"
                public class Simple {
                    public void Method() {
                        Console.WriteLine(""Hello"");
                    }
                }";

            _mockFileReader.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _mockFileClassifier.Setup(x => x.IsComplexityCheckable("test.cs"))
                .Returns(true);
            _mockFileReader.Setup(x => x.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(simpleCode);

            var rule = new RuleWithEnforcement("Cyclomatic complexity < 20", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("test.cs", rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ValidateAsync_WhenComplexityOverThreshold_ShouldFail()
        {
            // Arrange - Code with many branches
            var complexCode = @"
                public class Complex {
                    public void Method() {
                        if (a) { }
                        if (b) { }
                        if (c) { }
                        if (d) { }
                        if (e) { }
                        if (f) { }
                        if (g) { }
                        if (h) { }
                        if (i) { }
                        if (j) { }
                        if (k) { }
                        if (l) { }
                        if (m) { }
                        if (n) { }
                        if (o) { }
                        if (p) { }
                        if (q) { }
                        if (r) { }
                        if (s) { }
                        if (t) { }
                        if (u) { }
                    }
                }";

            _mockFileReader.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _mockFileClassifier.Setup(x => x.IsComplexityCheckable("test.cs"))
                .Returns(true);
            _mockFileReader.Setup(x => x.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(complexCode);

            var rule = new RuleWithEnforcement("Cyclomatic complexity < 20", Enforcement.WARN);

            // Act
            var result = await _validator.ValidateAsync("test.cs", rule, "AGENTS.md", "/root", CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("exceeds threshold", result.Message);
        }

        [Fact]
        public void SupportedRule_ShouldReturnCorrectRule()
        {
            // Assert
            Assert.Equal(Rule.CyclomaticComplexityUnder20, _validator.SupportedRule);
        }
    }
}
