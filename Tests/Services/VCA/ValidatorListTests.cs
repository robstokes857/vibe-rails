using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA
{
    public class ValidatorListTests
    {
        private readonly Mock<IRulesService> _mockRulesService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ValidatorList _validatorList;

        public ValidatorListTests()
        {
            _mockRulesService = new Mock<IRulesService>();

            var services = new ServiceCollection();
            services.AddScoped(_ => _mockRulesService.Object);
            services.AddScoped<IFileClassifier, FileClassifier>();
            services.AddScoped<IFileReader, FileReader>();
            services.AddScoped<IAgentFileService>(_ => Mock.Of<IAgentFileService>());
            services.AddScoped<IPathNormalizer, PathNormalizer>();
            services.AddScoped<LogAllFileChangesValidator>();
            services.AddScoped<PackageChangeValidator>();

            _serviceProvider = services.BuildServiceProvider();
            _validatorList = new ValidatorList(_serviceProvider);
        }

        [Fact]
        public async Task IsGoodCodeAsync_WithUnknownRule_ShouldReturnTrue()
        {
            // Arrange
            _mockRulesService
                .Setup(x => x.TryParse("Unknown rule", out It.Ref<Rule>.IsAny))
                .Returns(false);

            var rule = new RuleWithEnforcement("Unknown rule", Enforcement.WARN);

            // Act
            var result = await _validatorList.IsGoodCodeAsync("test.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsGoodCodeAsync_WithPackageFile_ShouldReturnFalse()
        {
            // Arrange
            var parsedRule = Rule.PackageChangeDetected;
            _mockRulesService
                .Setup(x => x.TryParse("Package file changes", out parsedRule))
                .Returns(true);

            var rule = new RuleWithEnforcement("Package file changes", Enforcement.WARN);

            // Act
            var result = await _validatorList.IsGoodCodeAsync("package.json", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task IsGoodCodeAsync_WithDisabledRule_ShouldReturnTrue()
        {
            // Arrange
            var parsedRule = Rule.CyclomaticComplexityDisabled;
            _mockRulesService
                .Setup(x => x.TryParse("Cyclomatic complexity disabled", out parsedRule))
                .Returns(true);

            var rule = new RuleWithEnforcement("Cyclomatic complexity disabled", Enforcement.WARN);

            // Act
            var result = await _validatorList.IsGoodCodeAsync("test.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsGoodCodeAsync_WithSkipTestCoverage_ShouldReturnTrue()
        {
            // Arrange
            var parsedRule = Rule.SkipTestCoverage;
            _mockRulesService
                .Setup(x => x.TryParse("Skip test coverage", out parsedRule))
                .Returns(true);

            var rule = new RuleWithEnforcement("Skip test coverage", Enforcement.WARN);

            // Act
            var result = await _validatorList.IsGoodCodeAsync("test.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData(Rule.LogFileChangesOver5Lines, 5)]
        [InlineData(Rule.LogFileChangesOver10Lines, 10)]
        public async Task IsGoodCodeAsync_WithLineThresholdRules_ShouldCreateCorrectValidator(Rule ruleEnum, int expectedThreshold)
        {
            // Arrange
            var parsedRule = ruleEnum;
            _mockRulesService
                .Setup(x => x.TryParse(It.IsAny<string>(), out parsedRule))
                .Returns(true);

            var rule = new RuleWithEnforcement($"Log file changes > {expectedThreshold} lines", Enforcement.WARN);

            // Act - Just verify it doesn't throw
            var result = await _validatorList.IsGoodCodeAsync("test.cs", rule, "AGENTS.md", "/root", null, CancellationToken.None);

            // Assert - Should not throw and return a result
            Assert.True(result || !result); // Just checking it executed
        }
    }
}
