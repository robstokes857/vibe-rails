using System.Text.Json;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class CodexModelCapabilitiesTests
{
    [Fact]
    public void EmbeddedCapabilitiesMatchTheBrowserCatalogAndBackendValidation()
    {
        using var stream = typeof(BaseLlmOptionsBuilder).Assembly.GetManifestResourceStream("CodexModelCapabilities.json");
        Assert.NotNull(stream);
        using var embedded = JsonDocument.Parse(stream);
        using var browser = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "wwwroot", "js", "modules", "codex-model-capabilities.json")));
        Assert.Equal(browser.RootElement.GetRawText(), embedded.RootElement.GetRawText());
        foreach (var model in embedded.RootElement.EnumerateObject())
        {
            var supported = model.Value.EnumerateArray().Select(value => value.GetString()).ToArray();
            foreach (var speed in new[] { "fast", "ultrafast", "turbo" })
                Assert.Equal(supported.Contains(speed), CodexModelCapabilities.SupportsSpeed($" {model.Name.ToUpperInvariant()} ", speed));
        }
        Assert.False(CodexModelCapabilities.SupportsSpeed("custom", "fast"));
        Assert.False(CodexModelCapabilities.SupportsSpeed(null, "fast"));
    }
}
