using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    private const string DesktopNodeRepl =
        "C:/Users/example/AppData/Local/OpenAI/Codex/runtimes/cua_node/old-build/bin/node_repl.exe";
    private const string DisableNodeRepl = "mcp_servers.node_repl.enabled=false";

    private static string DesktopMcpConfig(string command = DesktopNodeRepl, string settings = "") => $$"""
        model = "example-model"
        [mcp_servers.node_repl]
        command = '{{command}}'
        {{settings}}
        [mcp_servers.node_repl.env]
        SKY_CUA_NATIVE_PIPE = 'desktop-bridge'
        [mcp_servers.user_server]
        command = 'my-server'
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareSession_Codex_SkipsMissingCopiedDesktopBridgeForThisSession(bool basicString)
    {
        var config = DesktopMcpConfig();
        if (basicString)
            config = config.Replace($"'{DesktopNodeRepl}'",
                "\"" + DesktopNodeRepl.Replace("/", "\\\\") + "\"");
        var files = new Mock<IFileService>();
        var service = CreateDesktopMcpService(config, files: files);

        var prepared = await service.PrepareSessionAsync(LLM.Codex, "worker", ["--", "original prompt"]);

        Assert.Equal("--config", prepared.Argv![0]);
        Assert.Equal(DisableNodeRepl, prepared.Argv[1]);
        Assert.Equal(new[] { "--", "original prompt" }, prepared.Argv.Skip(2));
        Assert.Contains(DisableNodeRepl, prepared.LaunchCommand);
        Assert.EndsWith(prepared.LaunchCommand, prepared.Command);
        Assert.StartsWith("codex mcp add viberails-mcp -- ", Assert.Single(prepared.SetupCommands));
        files.Verify(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "", DesktopNodeRepl)]
    [InlineData(false, "enabled = false", DesktopNodeRepl)]
    [InlineData(false, "required = true", DesktopNodeRepl)]
    [InlineData(false, "", "node_repl")]
    [InlineData(false, "", "C:/tools/node_repl.exe")]
    public async Task PrepareSession_Codex_PreservesWorkingDisabledRequiredAndCustomServers(
        bool runtimeExists, string settings, string command)
    {
        var service = CreateDesktopMcpService(DesktopMcpConfig(command, settings), runtimeExists);

        var prepared = await service.PrepareSessionAsync(LLM.Codex, "worker", null);

        Assert.DoesNotContain(DisableNodeRepl, prepared.Argv!);
    }

    [Theory]
    [InlineData(LLM.Codex, null)]
    [InlineData(LLM.Claude, "worker")]
    public async Task PrepareSession_DesktopBridgeCheck_OnlyReadsCustomCodexEnvironments(LLM llm, string? envName)
    {
        var files = new Mock<IFileService>();
        var service = CreateDesktopMcpService(DesktopMcpConfig(), files: files);

        var prepared = await service.PrepareSessionAsync(llm, envName, null);

        Assert.DoesNotContain(DisableNodeRepl, prepared.Argv!);
        files.Verify(f => f.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[mcp_servers.other]\ncommand = 'missing.exe'")]
    [InlineData("[mcp_servers.node_repl]\ncommand = 'missing.exe'")]
    public async Task PrepareSession_Codex_DoesNotInventOrDisableUnrelatedServers(string config)
    {
        var prepared = await CreateDesktopMcpService(config).PrepareSessionAsync(LLM.Codex, "worker", null);

        Assert.DoesNotContain(DisableNodeRepl, prepared.Argv!);
    }

    [Theory]
    [InlineData("--config", "mcp_servers.node_repl.command='replacement'")]
    [InlineData("-c", "mcp_servers.node_repl.enabled=true")]
    [InlineData("--config=mcp_servers.node_repl.enabled=true", "")]
    [InlineData("-c=mcp_servers.node_repl.enabled=true", "")]
    [InlineData("-cmcp_servers.node_repl.enabled=true", "")]
    [InlineData("--profile", "custom")]
    [InlineData("--profile=custom", "")]
    [InlineData("-pcustom", "")]
    public async Task PrepareSession_Codex_RespectsExplicitDesktopBridgeConfiguration(string option, string value)
    {
        string[] args = value.Length > 0 ? [option, value] : [option];
        var prepared = await CreateDesktopMcpService(DesktopMcpConfig())
            .PrepareSessionAsync(LLM.Codex, "worker", args);

        Assert.Equal(args, prepared.Argv);
    }

    [Fact]
    public async Task PrepareSession_Codex_UnreadableConfigurationDoesNotAbortLaunch()
    {
        var service = CreateService(configureFiles: files =>
        {
            files.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
            files.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Config is being replaced"));
        });

        var prepared = await service.PrepareSessionAsync(LLM.Codex, "worker", null);

        Assert.Equal("codex", prepared.LaunchCommand);
    }

    private static CommandService CreateDesktopMcpService(
        string config, bool runtimeExists = false, Mock<IFileService>? files = null)
    {
        var path = Path.Combine(ParserConfigs.GetEnvPath(), "worker", "codex", "config.toml");
        files ??= new Mock<IFileService>();
        files.Setup(f => f.FileExists(path)).Returns(true);
        files.Setup(f => f.FileExists(DesktopNodeRepl)).Returns(runtimeExists);
        files.Setup(f => f.ReadAllTextAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(config);
        var configuredFiles = files;
        return CreateService(configureFiles: mock =>
        {
            mock.Setup(f => f.FileExists(It.IsAny<string>()))
                .Returns((string p) => configuredFiles.Object.FileExists(p));
            mock.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string p, CancellationToken ct) => configuredFiles.Object.ReadAllTextAsync(p, ct));
            mock.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()))
                .Callback((string p, string text, FileMode mode, FileShare share, CancellationToken ct) =>
                    configuredFiles.Object.WriteAllTextAsync(p, text, mode, share, ct));
        });
    }
}
