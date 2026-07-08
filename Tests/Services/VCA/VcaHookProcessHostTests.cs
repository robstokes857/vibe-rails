using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaHookProcessHostTests
{
    [Fact]
    public async Task RunAsync_PreviewMode_RendersHookProgressOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await VcaHookProcessHost.RunAsync(
            ["--vca-hook", "preview", "--demo-duration-ms", "1"],
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        var text = output.ToString();
        var firstLine = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).First();

        Assert.Equal(0, exitCode);
        Assert.Equal("VibeRails VCA: Preview check", firstLine);
        Assert.Contains("VibeRails VCA: Preview check", text);
        Assert.Contains("Reason: manual preview", text);
        Assert.Contains("PASS: VCA hook preview completed.", text);
    }
}
