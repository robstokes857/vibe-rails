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
        public async Task ValidateWithSourceAsync_LogAllChangesExemptsOnlyItsDeclaringFile()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rule-documentation-tests"));
            var source = Path.Combine(root, "nested", "vc.rules.md");
            var agents = new Mock<IAgentFileService>();
            agents.Setup(x => x.GetDocumentedFilesAsync(source, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var service = new RuleValidationService(new RulesService(), agents.Object);

            var result = await service.ValidateWithSourceAsync(
                ["nested/vc.rules.md", "nested/child/vc.rules.md", "nested/app.cs", "outside.txt"],
                [new RuleWithSource(new RuleWithEnforcement("Log all file changes", Enforcement.STOP), source)],
                root,
                TestContext.Current.CancellationToken);

            var validation = Assert.Single(result.Results);
            Assert.False(validation.Passed);
            Assert.Equal(["nested/child/vc.rules.md", "nested/app.cs"], validation.AffectedFiles);
        }

        [Theory]
        [InlineData("Log all file changes")]
        [InlineData("Log file changes > 5 lines")]
        [InlineData("Log file changes > 10 lines")]
        public async Task ValidateWithSourceAsync_DocumentationRulesPassWhenOnlyTheirOwnFileChanges(string ruleText)
        {
            var root = Path.Combine(Path.GetTempPath(), $"rule-documentation-{Guid.NewGuid():N}");
            var source = Path.Combine(root, "vc.rules.md");
            Directory.CreateDirectory(root);
            try
            {
                await File.WriteAllLinesAsync(source, Enumerable.Repeat("Policy notes", 20), TestContext.Current.CancellationToken);
                var agents = new Mock<IAgentFileService>();
                agents.Setup(x => x.GetDocumentedFilesAsync(source, It.IsAny<CancellationToken>()))
                    .ReturnsAsync([]);
                var service = new RuleValidationService(new RulesService(), agents.Object);

                var result = await service.ValidateWithSourceAsync(
                    ["./vc.rules.md"],
                    [new RuleWithSource(new RuleWithEnforcement(ruleText, Enforcement.STOP), source)],
                    root,
                    TestContext.Current.CancellationToken);

                Assert.True(Assert.Single(result.Results).Passed);
            }
            finally
            {
                File.Delete(source);
                Directory.Delete(root);
            }
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
