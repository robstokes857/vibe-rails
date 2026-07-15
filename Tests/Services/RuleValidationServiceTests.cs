using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests.Services
{
    public class RuleValidationServiceTests
    {
        private readonly RuleValidationService _service = new(
            new RulesService(),
            Mock.Of<IAgentFileService>());

        [Fact]
        public async Task ValidateAsync_FailsCoverageRule_WhenCodeFileHasNoMatchingTest()
        {
            var result = await _service.ValidateAsync(
                ["src/PaymentService.cs"],
                [new RuleWithEnforcement("Require test coverage minimum 80%", Enforcement.COMMIT)],
                "/repo",
                TestContext.Current.CancellationToken);

            var validation = Assert.Single(result.Results);
            Assert.False(validation.Passed);
            Assert.Equal(Enforcement.COMMIT, validation.Enforcement);
            Assert.Contains("80", validation.Message);
            Assert.Equal(["src/PaymentService.cs"], validation.AffectedFiles);
        }

        [Fact]
        public async Task ValidateAsync_PassesCoverageRule_WhenMatchingTestIsIncluded()
        {
            var result = await _service.ValidateAsync(
                ["src/PaymentService.cs", "tests/PaymentServiceTests.cs"],
                [new RuleWithEnforcement("Require test coverage minimum 80%", Enforcement.COMMIT)],
                "/repo",
                TestContext.Current.CancellationToken);

            var validation = Assert.Single(result.Results);
            Assert.True(validation.Passed);
            Assert.Null(validation.AffectedFiles);
        }
    }
}
