using Moq;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
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
                _mockFileAndRuleParser.Object, Mock.Of<IGitStagedSnapshotProvider>(), Mock.Of<IGitWorkingTreeSnapshotProvider>());
        }

        [Theory]
        [InlineData(false, "io")]
        [InlineData(true, "io")]
        [InlineData(false, "access")]
        [InlineData(true, "access")]
        [InlineData(false, "invalid")]
        [InlineData(true, "invalid")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        public async Task SnapshotFailureOnlyFailsQualityRules(bool stagedOnly, string failure)
        {
            Exception exception = failure switch
            {
                "io" => new IOException("snapshot unavailable"),
                "access" => new UnauthorizedAccessException("snapshot unavailable"),
                "invalid" => new InvalidOperationException("snapshot unavailable"),
                _ => new TimeoutException("git stalled")
            };
            var staged = new Mock<IGitStagedSnapshotProvider>(MockBehavior.Strict);
            var working = new Mock<IGitWorkingTreeSnapshotProvider>(MockBehavior.Strict);
            staged.Setup(p => p.CaptureAsync("/root", It.IsAny<CancellationToken>())).ThrowsAsync(exception);
            working.Setup(p => p.CaptureWorkingTreeAsync("/root", It.IsAny<CancellationToken>())).ThrowsAsync(exception);
            var service = new ValidationService(_mockValidatorList.Object, _mockFileAndRuleParser.Object,
                staged.Object, working.Object);
            var unrelated = new RuleWithEnforcement("Log all file changes", Enforcement.WARN);
            var context = new ValidationContext("preserved commit message", new() { ["custom"] = "preserved" });
            _mockFileAndRuleParser.Setup(p => p.GetFilesAndRulesAsync("/root", stagedOnly, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, List<VcaRuleWithSource>>
                {
                    ["test.cs"] = [new(new("Code quality minimum C", Enforcement.STOP), "vc.rules.md"),
                        new(unrelated, "vc.rules.md"), new(new("Code quality minimum B", Enforcement.COMMIT), "vc.rules.md")]
                });
            _mockValidatorList.Setup(v => v.IsGoodCodeAsync("test.cs", unrelated, "vc.rules.md", "/root", context,
                It.IsAny<CancellationToken>())).ReturnsAsync(false);

            var result = await service.ValidateAsync("/root", stagedOnly, context, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.TotalFiles);
            Assert.Equal(3, result.TotalRules);
            Assert.Equal(3, result.Results.Count);
            foreach (var index in new[] { 0, 2 })
            {
                var violation = result.Results[index];
                Assert.False(violation.Passed);
                Assert.Equal(index == 0 ? Enforcement.STOP : Enforcement.COMMIT, violation.Enforcement);
                Assert.Equal("test.cs", violation.FilePath);
                Assert.Equal("vc.rules.md", violation.SourceFile);
                Assert.Equal($"UNSUPPORTED: Code quality could not be evaluated: {exception.Message}", violation.Message);
            }
            Assert.Equal(unrelated.RuleText, result.Results[1].RuleName);
            _mockValidatorList.Verify(v => v.IsGoodCodeAsync("test.cs", unrelated, "vc.rules.md", "/root", context,
                It.IsAny<CancellationToken>()), Times.Once);
            _mockValidatorList.VerifyNoOtherCalls();
            staged.Verify(p => p.CaptureAsync("/root", It.IsAny<CancellationToken>()), stagedOnly ? Times.Once() : Times.Never());
            working.Verify(p => p.CaptureWorkingTreeAsync("/root", It.IsAny<CancellationToken>()), stagedOnly ? Times.Never() : Times.Once());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SnapshotCancellationPropagates(bool stagedOnly)
        {
            var staged = new Mock<IGitStagedSnapshotProvider>();
            var working = new Mock<IGitWorkingTreeSnapshotProvider>();
            staged.Setup(p => p.CaptureAsync("/root", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
            working.Setup(p => p.CaptureWorkingTreeAsync("/root", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
            var service = new ValidationService(_mockValidatorList.Object, _mockFileAndRuleParser.Object,
                staged.Object, working.Object);
            _mockFileAndRuleParser.Setup(p => p.GetFilesAndRulesAsync("/root", stagedOnly, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, List<VcaRuleWithSource>>
                {
                    ["test.cs"] = [new(new("Code quality minimum C", Enforcement.STOP), "vc.rules.md")]
                });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ValidateAsync("/root", stagedOnly, cancellationToken: TestContext.Current.CancellationToken));
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
                        "vc.rules.md")
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
                        "vc.rules.md")
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
            Assert.Equal("vc.rules.md", violation.SourceFile);
        }

        [Fact]
        public async Task ValidateAsync_WithMultipleFilesAndRules_ShouldProcessAll()
        {
            // Arrange
            var filesAndRules = new Dictionary<string, List<VcaRuleWithSource>>
            {
                ["test1.cs"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule 1", Enforcement.WARN), "vc.rules.md"),
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule 2", Enforcement.COMMIT), "vc.rules.md")
                },
                ["test2.cs"] = new List<VcaRuleWithSource>
                {
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule 3", Enforcement.STOP), "src/vc.rules.md")
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
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule A", Enforcement.WARN), "vc.rules.md"),
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule B", Enforcement.WARN), "vc.rules.md"),
                    new VcaRuleWithSource(new RuleWithEnforcement("Rule C", Enforcement.WARN), "vc.rules.md")
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
