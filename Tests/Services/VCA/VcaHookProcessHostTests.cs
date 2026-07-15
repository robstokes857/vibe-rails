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
        Assert.Equal("VibeRails · Git Guard — Preview", firstLine);
        Assert.Contains("[1/3]", text);
        Assert.Contains("PASS: VCA hook preview completed.", text);
        Assert.Contains("MintLint code health", text);
        Assert.Contains("Placeholder for automated workflows", text);
        Assert.Contains("[pass] Commit allowed", text);
    }
}
