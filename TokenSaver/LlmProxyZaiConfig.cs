using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// OpenCode / Z.AI (GLM) side of the local LLM proxy. Mirrors <see cref="LlmProxyClaudeConfig"/>
/// (Anthropic) and <see cref="LlmProxyCodexConfig"/> (OpenAI/Codex), but OpenCode is pointed at the
/// proxy through the <c>OPENCODE_CONFIG_CONTENT</c> env var (inline JSON config, high precedence)
/// rather than a dedicated base-URL env var or CLI <c>--config</c> args. That keeps VibeRails on
/// its launch-flag/env-var-only contract: no <c>opencode.json</c> file is written or managed.
///
/// The inline config overrides the <c>zai</c> and <c>xai</c> providers' <c>options.baseURL</c>
/// and <c>options.headers</c>; everything else (apiKey from global <c>auth.json</c>, models, etc.)
/// merges from the user's existing global/project OpenCode config. Credentials stay isolated:
/// <c>XDG_DATA_HOME</c> is left unchanged, per the OpenCode integration policy in
/// <c>runbooks/custom_envs/CLI_OPTIONS.md</c>.
///
/// Upstream: Z.AI's OpenAI-compatible Chat Completions API lives at
/// <c>https://api.z.ai/api/paas/v4</c> (verified against OpenCode's built-in <c>zai</c> provider,
/// which is backed by models.dev). The proxy strips <c>/llm/zai/</c> and forwards the remainder,
/// so a request to <c>/llm/zai/api/paas/v4/chat/completions</c> reaches
/// <c>https://api.z.ai/api/paas/v4/chat/completions</c>.
/// </summary>
public static class LlmProxyZaiConfig
{
    public const string ZaiProxyPath = "/llm/zai";

    /// <summary>Z.AI's public OpenAI-compatible API host. Chat Completions lives under
    /// <c>/api/paas/v4</c>.</summary>
    public const string UpstreamHost = "api.z.ai";

    // The API path prefix that follows the host and precedes /chat/completions. Combined with the
    // baseURL below, requests land at /llm/zai/api/paas/v4/chat/completions and the relay forwards
    // api/paas/v4/chat/completions to https://api.z.ai/api/paas/v4/chat/completions.
    public const string UpstreamApiPath = "/api/paas/v4";

    // Same header names the proxy auth-gate checks. Shared with the Claude/Codex paths so the proxy
    // validates one contract regardless of which CLI is calling.
    public const string SessionHeaderName = LlmProxyCodexConfig.SessionHeaderName;
    public const string TabHeaderName = LlmProxyCodexConfig.TabHeaderName;

    /// <summary>Env var OpenCode reads for inline JSON config overrides (high precedence).</summary>
    public const string ConfigContentVariable = "OPENCODE_CONFIG_CONTENT";

    /// <summary>
    /// Builds the <c>baseURL</c> value handed to OpenCode's <c>zai</c> provider. OpenCode appends
    /// <c>/chat/completions</c> to this, so the request path becomes
    /// <c>/llm/zai/api/paas/v4/chat/completions</c> and the route catches it under
    /// <see cref="ZaiProxyPath"/>.
    /// </summary>
    public static string BuildZaiBaseUrl(string apiBaseUrl) =>
        string.Concat(LlmProxyBaseUrl.Normalize(apiBaseUrl), ZaiProxyPath, UpstreamApiPath);

    /// <summary>
    /// Builds the <c>OPENCODE_CONFIG_CONTENT</c> JSON payload: overrides the <c>zai</c> and
    /// <c>xai</c> providers' <c>baseURL</c> to point at the local proxy and injects the session/tab
    /// auth headers the proxy validates. Token values are embedded literally (not via
    /// <c>{env:...}</c> substitution) so the contract does not depend on OpenCode resolving env
    /// refs inside <c>options.headers</c> — the same approach Claude's
    /// <c>ANTHROPIC_CUSTOM_HEADERS</c> takes. The value rides an env var, so the tokens never
    /// surface in the process command line.
    ///
    /// Emitted with a <see cref="Utf8JsonWriter"/> rather than <c>JsonSerializer.Serialize</c> on an
    /// anonymous object: the TokenSaver library is AOT-conscious (see the rewriters' no-DOM/no-
    /// reflection rule), and reflection-based serialization of anonymous types is not AOT-safe.
    /// </summary>
    public static string BuildOpencodeConfigContent(string apiBaseUrl, string sessionToken, string tabToken)
    {
        var baseUrl = BuildZaiBaseUrl(apiBaseUrl);

        // Sized for the two provider blocks: each carries a baseURL plus the session and tab tokens
        // (512-bit → 86 base64url chars each), so the payload embeds four token copies in total.
        // Grows if needed via the pooled writer.
        using var buffer = new PooledBufferWriter(1024);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        });

        writer.WriteStartObject();
        writer.WritePropertyName("provider"u8);
        writer.WriteStartObject();
        WriteProviderOverride(writer, "zai"u8, baseUrl, sessionToken, tabToken);
        WriteProviderOverride(
            writer,
            "xai"u8,
            LlmProxyXaiConfig.BuildXaiBaseUrl(apiBaseUrl),
            sessionToken,
            tabToken);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }

    private static void WriteProviderOverride(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> providerName,
        string baseUrl,
        string sessionToken,
        string tabToken)
    {
        writer.WritePropertyName(providerName);
        writer.WriteStartObject();
        writer.WritePropertyName("options"u8);
        writer.WriteStartObject();
        writer.WriteString("baseURL"u8, baseUrl);
        writer.WritePropertyName("headers"u8);
        writer.WriteStartObject();
        writer.WriteString(SessionHeaderName, sessionToken);
        writer.WriteString(TabHeaderName, tabToken);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
