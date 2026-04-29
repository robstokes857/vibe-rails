using Moq;
using VibeRails.Services.BertV2;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.BertV2;

public class BertV2InputDBServiceTests
{
    private readonly Mock<IBertV2InputService> _mockInputService;
    private readonly Mock<IGetUserText> _mockUserText;
    private readonly BertV2InputDBService _service;

    public BertV2InputDBServiceTests()
    {
        _mockInputService = new Mock<IBertV2InputService>();
        _mockUserText = new Mock<IGetUserText>();
        _service = new BertV2InputDBService(_mockInputService.Object, _mockUserText.Object);
    }

    [Fact]
    public async Task CaptureUserInput_SkipsEmbedding_WhenTextIsEmpty()
    {
        _mockUserText
            .Setup(x => x.GetTextForInputIdAsync(1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        await _service.CaptureUserInputAsync("session-1", 1, TestContext.Current.CancellationToken);

        _mockInputService.Verify(
            x => x.Capture(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureUserInput_EmbedsText_WhenAvailable()
    {
        _mockUserText
            .Setup(x => x.GetTextForInputIdAsync(1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("add search functionality");

        await _service.CaptureUserInputAsync("session-1", 1, TestContext.Current.CancellationToken);

        _mockInputService.Verify(
            x => x.Capture("session-1", 1, "add search functionality"),
            Times.Once);
    }
}
