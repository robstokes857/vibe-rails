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

        [Fact]
        public async Task ValidateWithSourceAsync_FileLockUsesTheAgentDirectoryAsItsBase()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rule-validation-locks"));
            var source = Path.Combine(root, "nested", "vc.rules.md");

            var result = await _service.ValidateWithSourceAsync(
                ["nested/config/settings.json", "outside.txt"],
                [new RuleWithSource(
                    new RuleWithEnforcement("File Lock('config/settings.json')", Enforcement.STOP),
                    source)],
                root,
                TestContext.Current.CancellationToken);

            var validation = Assert.Single(result.Results);
            Assert.False(validation.Passed);
            Assert.Equal(["nested/config/settings.json"], validation.AffectedFiles);
        }

        [Fact]
        public async Task ValidateWithSourceAsync_DirectoryLockIsPathBoundaryAware()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rule-validation-locks"));
            var source = Path.Combine(root, "vc.rules.md");

            var result = await _service.ValidateWithSourceAsync(
                ["locked-old/file.txt"],
                [new RuleWithSource(
                    new RuleWithEnforcement("Directory Lock('locked')", Enforcement.STOP),
                    source)],
                root,
                TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(result.Results).Passed);
        }
    }
}
