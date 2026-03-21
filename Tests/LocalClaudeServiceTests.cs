using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class LocalClaudeServiceTests
{
    private readonly Mock<IShellService> _shell = new();
    private readonly LocalClaudeService _sut;

    public LocalClaudeServiceTests()
    {
        _shell.Setup(s => s.RunAsync(It.IsAny<CancellationToken>(), It.IsAny<string[]>()))
              .ReturnsAsync("ok");
        _sut = new LocalClaudeService(_shell.Object);
    }

    [Fact]
    public async Task ProcessAsync_BasicPrompt_PassesCorrectArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a[0] == "claude" && a[1] == "--print" && a[2] == "hello")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithModel_IncludesModelArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, model: "claude-opus-4-6");

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--model") && a.Contains("claude-opus-4-6"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithSystemPrompt_IncludesSystemPromptArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, systemPrompt: "be concise");

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--system-prompt") && a.Contains("be concise"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithMaxTurns_IncludesMaxTurnsArgs()
    {
        await _sut.ProcessAsync("hello", CancellationToken.None, maxTurns: 3);

        _shell.Verify(s => s.RunAsync(
            It.IsAny<CancellationToken>(),
            It.Is<string[]>(a => a.Contains("--max-turns") && a.Contains("3"))),
            Times.Once);
    }
}
