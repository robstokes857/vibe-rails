using Moq;
using TokenSaver;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("corporate")]
    public async Task Codex_PreservesSavedProviderAndCredentials(string? envName)
    {
        const string config = """
            model_provider = "enterprise"
            [model_providers.enterprise]
            name = "Enterprise"
            base_url = "https://gateway.example.test/v1"
            env_key = "OPENAI_API_KEY"
            supports_websockets = false
            """;
        var configPath = envName is null
            ? Path.Combine(Path.GetTempPath(), "viberails-grok-tests", ".codex", "config.toml")
            : Path.Combine(ParserConfigs.GetEnvPath(), envName, "codex", "config.toml");
        Mock<VibeRails.Interfaces.IFileService>? filesMock = null;
        var service = CreateService(codexLlmProxyEnabled: true, configureFiles: files =>
        {
            filesMock = files;
            files.Setup(f => f.FileExists(configPath)).Returns(true);
            files.Setup(f => f.ReadAllTextAsync(configPath, It.IsAny<CancellationToken>())).ReturnsAsync(config);
        });

        var prepared = await service.PrepareSessionAsync(LLM.Codex, envName, null, "hello", sessionId: "probe");

        Assert.DoesNotContain(prepared.Argv!, arg => arg.StartsWith("model_provider", StringComparison.Ordinal));
        Assert.DoesNotContain(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.False(prepared.Environment.ContainsKey(LocalLlmProxyContext.SessionTokenVariable));
        Assert.False(prepared.Environment.ContainsKey("OPENAI_API_KEY")); // inherited, never replaced
        Assert.Contains("mcp_servers.viberails-mcp.env_vars", prepared.LaunchCommand);
        filesMock!.Verify(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("model_provider = 'enterprise' # selected gateway", true)]
    [InlineData("profile = 'work'\n[profiles.work]\nmodel_provider = 'enterprise'", true)]
    [InlineData("openai_base_url = 'https://gateway.example.test/v1'", true)]
    [InlineData("model_provider = 'openai'", false)]
    [InlineData("# model_provider = 'enterprise'\n[model_providers.enterprise]\nname = 'unused'", false)]
    [InlineData("[profiles.unused]\nmodel_provider = 'enterprise'", false)]
    public void Codex_OnlySelectedProviderConfigurationBypassesProxy(string config, bool expected)
    {
        Assert.Equal(expected, CodexLlmCliEnvironment.HasOwnProviderConfiguration(config));
    }
}
