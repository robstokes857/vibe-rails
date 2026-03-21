using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class LocalGeminiServiceTests
{
    private readonly Mock<IShellService> _shell = new();
    private readonly LocalGeminiService _sut;

    public LocalGeminiServiceTests()
    {
        _shell.Setup(s => s.RunAsync(It.IsAny<CancellationToken>(), It.IsAny<string[]>()))
              .ReturnsAsync("ok");
        _sut = new LocalGeminiService(_shell.Object);
    }

    [Fact]
    public async Task ProcessAsync_BasicPrompt_PassesCorrectArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a[0] == "gemini" && a[1] == "-p" && a[2] == "hello")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithModel_IncludesModelArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, model: "gemini-2.5-pro");

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--model") && a.Contains("gemini-2.5-pro"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithAllowedTools_IncludesAllowedToolsArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, allowedTools: "code_execution");

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--allowed-tools") && a.Contains("code_execution"))),
            Times.Once);
    }
}
