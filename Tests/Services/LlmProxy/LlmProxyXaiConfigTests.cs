using TokenSaver;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxyXaiConfigTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4321", "http://127.0.0.1:4321/llm/xai/v1")]
    [InlineData("http://127.0.0.1:4321/", "http://127.0.0.1:4321/llm/xai/v1")]
    [InlineData("http://127.0.0.1:4321///", "http://127.0.0.1:4321/llm/xai/v1")]
    public void BuildXaiBaseUrl_NormalizesTrailingSlashes(string input, string expected)
    {
        Assert.Equal(expected, LlmProxyXaiConfig.BuildXaiBaseUrl(input));
    }
}
