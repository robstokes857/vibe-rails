using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using Xunit;

using VcaRuleWithSource = VibeRails.Services.VCA.RuleWithSource;

namespace Tests.Services.VCA
{
    public class ValidationServiceIntegrationTests
    {
        private readonly Mock<IFileAndRuleParser> _mockFileAndRuleParser;
        private readonly Mock<IValidatorList> _mockValidatorList;
        private readonly VibeRails.Services.VCA.ValidationService _validationService;

        public ValidationServiceIntegrationTests()
        {
            _mockFileAndRuleParser = new Mock<IFileAndRuleParser>();
            _mockValidatorList = new Mock<IValidatorList>();
            _validationService = new VibeRails.Services.VCA.ValidationService(
                _mockValidatorList.Object,
                _mockFileAndRuleParser.Object);
        }

        [Fact]
        public async Task ValidateAsync_WithNoFiles_ShouldReturnEmptyResults()
        {
            // Arrange
            _mockFileAndRuleParser
                .Setup(x => x.GetFilesAndRulesAsync("/root", false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, List<VcaRuleWithSource>>());

            // Act
            var result = await _validationService.ValidateAsync("/root", false, null, CancellationToken.None);

            // Assert
            Assert.Empty(result.Results);
            Assert.Equal(0, result.TotalFiles);
            Assert.Equal(0, result.TotalRules);
        }

        [Fact]
        public async Task ValidateAsync_WithPassingRules_ShouldReturnNoViolations()
        {
            // Arrange
            var filesAndRules = new Dictionary<string, List<VcaRuleWithSource>>
            {
                ["test.cs"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(
                        new RuleWithEnforcement("Log all file changes", Enforcement.WARN),
                        "AGENTS.md")
                }
            };

            _mockFileAndRuleParser
                .Setup(x => x.GetFilesAndRulesAsync("/root", false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filesAndRules);

            _mockValidatorList
                .Setup(x => x.IsGoodCodeAsync(It.IsAny<string>(), It.IsAny<RuleWithEnforcement>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ValidationContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Act
            var result = await _validationService.ValidateAsync("/root", false, null, CancellationToken.None);

            // Assert
            Assert.Empty(result.Results);
            Assert.Equal(1, result.TotalFiles);
            Assert.Equal(1, result.TotalRules);
        }

        [Fact]
        public async Task ValidateAsync_WithFailingRule_ShouldReturnViolation()
        {
            // Arrange
            var filesAndRules = new Dictionary<string, List<VcaRuleWithSource>>
            {
                ["package.json"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(
                        new RuleWithEnforcement("Package file changes", Enforcement.STOP),
                        "AGENTS.md")
                }
            };

            _mockFileAndRuleParser
                .Setup(x => x.GetFilesAndRulesAsync("/root", true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filesAndRules);

            _mockValidatorList
                .Setup(x => x.IsGoodCodeAsync("package.json", It.IsAny<RuleWithEnforcement>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ValidationContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            // Act
            var result = await _validationService.ValidateAsync("/root", true, null, CancellationToken.None);

            // Assert
            Assert.Single(result.Results);
            Assert.Equal(1, result.TotalFiles);
            Assert.Equal(1, result.TotalRules);

            var violation = result.Results[0];
            Assert.False(violation.Passed);
            Assert.Equal("package.json", violation.FilePath);
            Assert.Equal("Package file changes", violation.RuleName);
            Assert.Equal(Enforcement.STOP, violation.Enforcement);
            Assert.Equal("AGENTS.md", violation.SourceFile);
        }

        [Fact]
        public async Task ValidateAsync_WithMultipleFilesAndRules_ShouldProcessAll()
        {
            // Arrange
            var filesAndRules = new Dictionary<string, List<VcaRuleWithSource>>
            {
                ["test1.cs"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule 1", Enforcement.WARN), "AGENTS.md"),
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule 2", Enforcement.COMMIT), "AGENTS.md")
                },
                ["test2.cs"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule 3", Enforcement.STOP), "src/AGENTS.md")
                }
            };

            _mockFileAndRuleParser
                .Setup(x => x.GetFilesAndRulesAsync("/root", false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filesAndRules);

            // Rule 2 fails for test1.cs
            _mockValidatorList
                .Setup(x => x.IsGoodCodeAsync("test1.cs", It.Is<RuleWithEnforcement>(r => r.RuleText == "Rule 2"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ValidationContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            // All others pass
            _mockValidatorList
                .Setup(x => x.IsGoodCodeAsync(It.IsAny<string>(), It.Is<RuleWithEnforcement>(r => r.RuleText != "Rule 2"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ValidationContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Act
            var result = await _validationService.ValidateAsync("/root", false, null, CancellationToken.None);

            // Assert
            Assert.Single(result.Results); // Only one failure
            Assert.Equal(2, result.TotalFiles);
            Assert.Equal(3, result.TotalRules);

            var violation = result.Results[0];
            Assert.Equal("test1.cs", violation.FilePath);
            Assert.Equal("Rule 2", violation.RuleName);
            Assert.Equal(Enforcement.COMMIT, violation.Enforcement);
        }

        [Fact]
        public async Task ValidateAsync_WithStagedOnlyFlag_ShouldPassToParser()
        {
            // Arrange
            _mockFileAndRuleParser
                .Setup(x => x.GetFilesAndRulesAsync("/root", true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, List<VcaRuleWithSource>>());

            // Act
            await _validationService.ValidateAsync("/root", true, null, CancellationToken.None);

            // Assert
            _mockFileAndRuleParser.Verify(
                x => x.GetFilesAndRulesAsync("/root", true, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ValidateAsync_WithNoDuplicateRules_ShouldCountAllRules()
        {
            // Arrange
            var filesAndRules = new Dictionary<string, List<VcaRuleWithSource>>
            {
                ["test.cs"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule A", Enforcement.WARN), "AGENTS.md"),
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule B", Enforcement.WARN), "AGENTS.md"),
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule C", Enforcement.WARN), "AGENTS.md")
                }
            };

            _mockFileAndRuleParser
                .Setup(x => x.GetFilesAndRulesAsync("/root", false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filesAndRules);

            _mockValidatorList
                .Setup(x => x.IsGoodCodeAsync(It.IsAny<string>(), It.IsAny<RuleWithEnforcement>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ValidationContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Act
            var result = await _validationService.ValidateAsync("/root", false, null, CancellationToken.None);

            // Assert
            Assert.Equal(1, result.TotalFiles);
            Assert.Equal(3, result.TotalRules);
        }
    }
}
