using Moq;
using VibeRails.Services.BertV2;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.BertV2;

public class BertV2InputDBServiceTests
{
    private readonly Mock<IBertV2InputService> _mockInputService;
    private readonly Mock<IGetCleanedUserText> _mockUserTextOutput;
    private readonly BertV2InputDBService _service;

    public BertV2InputDBServiceTests()
    {
        _mockInputService = new Mock<IBertV2InputService>();
        _mockUserTextOutput = new Mock<IGetCleanedUserText>();
        _service = new BertV2InputDBService(_mockInputService.Object, _mockUserTextOutput.Object);
    }

    [Fact]
    public async Task CaptureUserInput_SkipsEmbedding_WhenCleanedTextIsEmpty()
    {
        _mockUserTextOutput
            .Setup(x => x.GetTextForInputIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        await _service.CaptureUserInputAsync("session-1", 1, TestContext.Current.CancellationToken);

        _mockInputService.Verify(
            x => x.Capture(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureUserInput_EmbedsCleanedText_WhenAvailable()
    {
        _mockUserTextOutput
            .Setup(x => x.GetTextForInputIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("add search functionality");

        await _service.CaptureUserInputAsync("session-1", 1, TestContext.Current.CancellationToken);

        _mockInputService.Verify(
            x => x.Capture("session-1", 1, "add search functionality"),
            Times.Once);
    }
}
