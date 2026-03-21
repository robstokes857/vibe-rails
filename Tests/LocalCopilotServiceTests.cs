using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class LocalCopilotServiceTests
{
    private readonly Mock<IShellService> _shell = new();
    private readonly LocalCopilotService _sut;

    public LocalCopilotServiceTests()
    {
        _shell.Setup(s => s.RunAsync(It.IsAny<CancellationToken>(), It.IsAny<string[]>()))
              .ReturnsAsync("ok");
        _sut = new LocalCopilotService(_shell.Object);
    }

    [Fact]
    public async Task ProcessAsync_BasicPrompt_PassesCorrectArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a[0] == "copilot" && a[1] == "-p" && a[2] == "hello")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithJsonOutput_IncludesJsonFlag()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, jsonOutput: true);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--output=json"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithYolo_IncludesYoloFlag()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, yolo: true);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--yolo"))),
            Times.Once);
    }
}
