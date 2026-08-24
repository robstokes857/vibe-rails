using TokenSaver;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxyGrokConfigTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4321", "http://127.0.0.1:4321/llm/cli-chat/v1")]
    [InlineData("http://127.0.0.1:4321/", "http://127.0.0.1:4321/llm/cli-chat/v1")]
    [InlineData("http://127.0.0.1:4321///", "http://127.0.0.1:4321/llm/cli-chat/v1")]
    public void BuildGrokProxyEnvironment_Subscription_SetsOnlyChatProxyUrl(string apiBaseUrl, string expected)
    {
        var env = LlmProxyGrokConfig.BuildGrokProxyEnvironment(apiBaseUrl);

        Assert.Equal(expected, env[LlmProxyGrokConfig.ChatProxyBaseUrlVariable]);
        Assert.False(env.ContainsKey(LlmProxyGrokConfig.ModelsBaseUrlVariable));
        Assert.Single(env);
    }

    [Fact]
    public void BuildGrokProxyEnvironment_Api_SetsChatAndModelsUrls()
    {
        var env = LlmProxyGrokConfig.BuildGrokProxyEnvironment(
            "http://127.0.0.1:4321",
            CodexLlmProxySettings.ModeApi);

        Assert.Equal(
            "http://127.0.0.1:4321/llm/cli-chat/v1",
            env[LlmProxyGrokConfig.ChatProxyBaseUrlVariable]);
        Assert.Equal(
            "http://127.0.0.1:4321/llm/cli-chat/v1",
            env[LlmProxyGrokConfig.ModelsBaseUrlVariable]);
    }

    [Fact]
    public void BuildGrokProxyEnvironment_DoesNotEmbedTokens()
    {
        var env = LlmProxyGrokConfig.BuildGrokProxyEnvironment("http://127.0.0.1:4321");

        Assert.All(env.Values, value =>
        {
            Assert.DoesNotContain("viberails_session", value);
            Assert.DoesNotContain("session", value, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ResolveUserConfigPath_PrefersGrokHomeOverUserProfile()
    {
        var path = LlmProxyGrokConfig.ResolveUserConfigPath(
            Path.Combine("C:", "Users", "me"),
            Path.Combine("D:", "custom-grok"));

        Assert.Equal(Path.Combine("D:", "custom-grok", "config.toml"), path);
    }

    [Fact]
    public void ResolveUserConfigPath_UsesDefaultDotGrokWhenHomeUnset()
    {
        var home = Path.Combine("C:", "Users", "me");
        var path = LlmProxyGrokConfig.ResolveUserConfigPath(home, grokHome: null);

        Assert.Equal(Path.Combine(home, ".grok", "config.toml"), path);
    }

    [Fact]
    public void ResolveUserConfigPath_ReturnsNullWhenNeitherHomeIsSet()
    {
        Assert.Null(LlmProxyGrokConfig.ResolveUserConfigPath(null, null));
        Assert.Null(LlmProxyGrokConfig.ResolveUserConfigPath("  ", ""));
    }

    [Fact]
    public void MergeEnvHttpHeaders_WritesEveryMappedModelSectionOnEmptyFile()
    {
        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders("");

        // Dotted model names must be quoted: TOML splits unquoted dotted keys, so
        // [model.grok-4.6] addresses the phantom model."grok-4"."6" and grok never
        // attaches the headers (verified against grok 1.0.5).
        Assert.Contains("[model.\"grok-4.6\"]", merged);
        Assert.Contains("[model.grok-build]", merged);
        Assert.Contains("[model.\"grok-4.5\"]", merged);
        Assert.DoesNotContain("[model.grok-4.6]", merged);
        Assert.Contains(LlmProxyGrokConfig.SessionHeaderName, merged);
        Assert.Contains(LlmProxyGrokConfig.TabHeaderName, merged);
        Assert.Contains(LlmProxyGrokConfig.SessionTokenVariable, merged);
        Assert.Contains(LlmProxyGrokConfig.TabTokenVariable, merged);
        Assert.DoesNotContain("base_url", merged);
        Assert.DoesNotContain("cli_chat_proxy_base_url", merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_NeverWritesTokenValues()
    {
        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(null);

        Assert.DoesNotContain("viberails_session: ", merged);
        Assert.DoesNotContain("Bearer ", merged);
        Assert.Contains($"\"{LlmProxyGrokConfig.SessionHeaderName}\" = \"{LlmProxyGrokConfig.SessionTokenVariable}\"", merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_IsIdempotentWhenMappingsAlreadyPresent()
    {
        var first = LlmProxyGrokConfig.MergeEnvHttpHeaders("");
        var second = LlmProxyGrokConfig.MergeEnvHttpHeaders(first);

        Assert.Equal(first, second);
    }

    [Fact]
    public void MergeEnvHttpHeaders_IsIdempotentAgainstGrokNormalizedNestedTables()
    {
        // grok's own serializer (any `grok mcp add` rewrites config.toml) normalizes our
        // inline table into a quoted nested table. A relaunch must not re-append anything.
        const string grokNormalized = """
            [model."grok-4.6".env_http_headers]
            viberails_session = "VIBERAILS_LLM_PROXY_SESSION_TOKEN"
            viberails_tab = "VIBERAILS_LLM_PROXY_TAB_TOKEN"

            [model.grok-build.env_http_headers]
            viberails_session = "VIBERAILS_LLM_PROXY_SESSION_TOKEN"
            viberails_tab = "VIBERAILS_LLM_PROXY_TAB_TOKEN"

            [model."grok-4.5".env_http_headers]
            viberails_session = "VIBERAILS_LLM_PROXY_SESSION_TOKEN"
            viberails_tab = "VIBERAILS_LLM_PROXY_TAB_TOKEN"
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(grokNormalized);

        Assert.Equal(grokNormalized, merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_RemovesPhantomSectionsAnEarlierVibeRailsWrote()
    {
        // The exact on-disk state the unquoted writer left behind after grok normalized it:
        // an unquoted dotted header is a phantom table grok never reads. grok-build has no
        // dot, so its unquoted nested table is correct and must survive.
        const string legacy = """
            [model.grok-4.6.env_http_headers]
            viberails_session = "VIBERAILS_LLM_PROXY_SESSION_TOKEN"
            viberails_tab = "VIBERAILS_LLM_PROXY_TAB_TOKEN"

            [model.grok-build.env_http_headers]
            viberails_session = "VIBERAILS_LLM_PROXY_SESSION_TOKEN"
            viberails_tab = "VIBERAILS_LLM_PROXY_TAB_TOKEN"
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(legacy);

        Assert.DoesNotContain("[model.grok-4.6.env_http_headers]", merged);
        Assert.Contains("[model.grok-build.env_http_headers]", merged);
        Assert.Contains("[model.\"grok-4.6\"]", merged);
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-4.6"));
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-build"));
    }

    [Fact]
    public void MergeEnvHttpHeaders_RemovesPhantomInlineSectionFromTheOldWriter()
    {
        // The pre-normalization shape the old writer produced directly.
        const string legacy = """
            [model.grok-4.6]
            env_http_headers = { "viberails_session" = "VIBERAILS_LLM_PROXY_SESSION_TOKEN", "viberails_tab" = "VIBERAILS_LLM_PROXY_TAB_TOKEN" }
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(legacy);

        Assert.DoesNotContain("[model.grok-4.6]" + "\n", merged.Replace("\r\n", "\n"));
        Assert.Contains("[model.\"grok-4.6\"]", merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_LeavesPhantomSectionsHoldingForeignContent()
    {
        // A phantom section that carries anything beyond our own mapping is not ours to
        // delete -- it stays (inert), and the correct quoted section is still written.
        const string legacy = """
            [model.grok-4.6.env_http_headers]
            viberails_session = "VIBERAILS_LLM_PROXY_SESSION_TOKEN"
            "X-Tenant" = "TENANT_TOKEN"
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(legacy);

        Assert.Contains("[model.grok-4.6.env_http_headers]", merged);
        Assert.Contains("TENANT_TOKEN", merged);
        Assert.Contains("[model.\"grok-4.6\"]", merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_PreservesUnrelatedContent()
    {
        const string existing = """
            [cli]
            installer = "internal"

            [models]
            default = "grok-4.6"
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(existing);

        Assert.Contains("[cli]", merged);
        Assert.Contains("installer = \"internal\"", merged);
        Assert.Contains("default = \"grok-4.6\"", merged);
        Assert.Contains("[model.\"grok-4.6\"]", merged);
        Assert.Contains("[model.grok-build]", merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_InsertsHeadersIntoExistingModelSection()
    {
        const string existing = """
            [model."grok-4.6"]
            temperature = 0.2

            [model.grok-build]
            name = "Build"
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(existing);

        Assert.Contains("temperature = 0.2", merged);
        Assert.Contains("name = \"Build\"", merged);
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-4.6"));
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-build"));
        Assert.Equal(1, CountOccurrences(merged, "[model.\"grok-4.6\"]"));
        Assert.Equal(1, CountOccurrences(merged, "[model.grok-build]"));
    }

    [Fact]
    public void MergeEnvHttpHeaders_MergesKeysWithoutDroppingExistingHeaders()
    {
        const string existing = """
            [model."grok-4.6"]
            env_http_headers = { "X-Tenant" = "TENANT_TOKEN" }

            [model.grok-build]
            env_http_headers = { "viberails_session" = "VIBERAILS_LLM_PROXY_SESSION_TOKEN" }
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(existing);

        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-4.6"));
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-build"));
        Assert.Contains("TENANT_TOKEN", merged);
    }

    [Fact]
    public void MergeEnvHttpHeaders_AppendsMissingKeysOnNestedHeaderTable()
    {
        const string existing = """
            [model."grok-4.6".env_http_headers]
            "X-Tenant" = "TENANT_TOKEN"

            [model.grok-build]
            """;

        var merged = LlmProxyGrokConfig.MergeEnvHttpHeaders(existing);

        Assert.Contains("\"X-Tenant\" = \"TENANT_TOKEN\"", merged);
        Assert.Contains(LlmProxyGrokConfig.SessionTokenVariable, merged);
        Assert.Contains(LlmProxyGrokConfig.TabTokenVariable, merged);
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-4.6"));
        Assert.True(LlmProxyGrokConfig.ContainsMappedEnvHttpHeaders(merged, "grok-build"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
