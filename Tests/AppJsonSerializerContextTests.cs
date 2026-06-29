using System.Text.Json;
using VibeRails.DTOs;
using Xunit;

namespace Tests;

public sealed class AppJsonSerializerContextTests
{
    [Fact]
    public void IncludesUpdateComputerNameDto_ForMinimalApiBodyBinding()
    {
        var json = JsonSerializer.Serialize(
            new UpdateComputerNameDto("build-box"),
            AppJsonSerializerContext.Default.UpdateComputerNameDto);

        Assert.Equal("""{"computerName":"build-box"}""", json);
    }
}
