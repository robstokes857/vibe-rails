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
            TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("VibeRails VCA - Preview", text);
        Assert.Contains("Reason: manual preview", text);
        Assert.Contains("PASS: VCA hook preview completed.", text);
    }
}
