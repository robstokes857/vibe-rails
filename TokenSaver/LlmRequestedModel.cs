using System.Text.Json;

namespace TokenSaver;

/// <summary>
/// What a proxied request asked for: the model, how hard it asked that model to think, and which
/// service tier it asked to be served on. Read from the request body as the CLI sent it, so the
/// exchange log can split savings and agent behavior by model without re-parsing every body.
///
/// One scan covers every body shape the proxies relay, because the fields sit in different places:
///   • <see cref="Model"/> is the top-level <c>model</c> in Anthropic Messages, OpenAI Responses
///     and Chat Completions alike.
///   • <see cref="ReasoningEffort"/> is <c>reasoning.effort</c> (Responses), top-level
///     <c>reasoning_effort</c> (Chat Completions) or <c>output_config.effort</c> (Claude Code's
///     Messages requests).
///   • <see cref="ServiceTier"/> is the top-level <c>service_tier</c>. Codex sends
///     <c>"priority"</c> for its fast speeds and omits the field otherwise.
///
/// Values are recorded verbatim, never normalized. A value is null when the body is empty, is not
/// valid JSON, or lacks the field. Nothing here can fail a relay or cost it a row: an unreadable
/// body reads as all-null.
/// </summary>
internal readonly record struct LlmRequestedModel(string? Model, string? ReasoningEffort, string? ServiceTier)
{
    internal static LlmRequestedModel Read(ReadOnlySpan<byte> utf8Body)
    {
        if (utf8Body.IsEmpty)
            return default;

        try
        {
            var reader = new Utf8JsonReader(utf8Body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return default;

            string? model = null, effort = null, tier = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("model"u8))
                    model = ReadString(ref reader);
                else if (reader.ValueTextEquals("service_tier"u8))
                    tier = ReadString(ref reader);
                // Three places can carry the effort; one without it never erases another's.
                else if (reader.ValueTextEquals("reasoning_effort"u8))
                    effort = ReadString(ref reader) ?? effort;
                else if (reader.ValueTextEquals("reasoning"u8) || reader.ValueTextEquals("output_config"u8))
                    effort = ReadEffort(ref reader) ?? effort;
                else
                    SkipValue(ref reader);
            }

            // All or nothing: a body that does not parse to its end is rejected upstream, so the
            // fields read before the fault were never really asked for. Truncated input throws
            // above; trailing bytes after the root object throw here.
            reader.Read();
            return new LlmRequestedModel(model, effort, tier);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // JsonException: not JSON. InvalidOperationException: a string that is not valid UTF-8.
            return default;
        }
    }

    private static string? ReadEffort(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? effort = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("effort"u8))
                effort = ReadString(ref reader);
            else
                SkipValue(ref reader);
        }

        return effort;
    }

    private static string? ReadString(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();

        reader.Skip();
        return null;
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        reader.Read();
        reader.Skip();
    }
}
