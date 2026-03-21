using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class LocalCodexServiceTests
{
    private readonly Mock<IShellService> _shell = new();
    private readonly LocalCodexService _sut;

    public LocalCodexServiceTests()
    {
        _shell.Setup(s => s.RunAsync(It.IsAny<CancellationToken>(), It.IsAny<string[]>()))
              .ReturnsAsync("ok");
        _sut = new LocalCodexService(_shell.Object);
    }

    [Fact]
    public async Task ProcessAsync_BasicPrompt_PassesCorrectArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a[0] == "codex" && a[1] == "exec" && a[2] == "hello")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithModel_IncludesModelArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, model: "gpt-4o");

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--model") && a.Contains("gpt-4o"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithJsonOutput_IncludesJsonFlag()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, jsonOutput: true);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--json"))),
            Times.Once);
    }
}
