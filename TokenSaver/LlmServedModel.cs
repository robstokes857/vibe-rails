using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TokenSaver;

/// <summary>
/// The model reported by the upstream response, which can differ from the requested alias.
/// Read only from the retained capture after the relay ends. SSE needs only the first complete
/// model-bearing event, so a later capture truncation does not lose the model. Unsupported,
/// malformed and error responses have no served model; values are never inferred from requests.
/// </summary>
internal static class LlmServedModel
{
    internal static string? Read(ReadOnlySpan<byte> body, string? mediaType, int statusCode)
    {
        if (body.IsEmpty || statusCode is < 200 or >= 300)
            return null;

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return ReadSse(body);

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || (mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false))
            return ReadJson(body, eventName: null);

        return null;
    }

    private static string? ReadSse(ReadOnlySpan<byte> body)
    {
        // SSE permits a BOM, LF, CRLF or CR, comments and several data lines per event.
        if (body.StartsWith("\uFEFF"u8))
            body = body[3..];

        string eventName = "";
        ReadOnlySpan<byte> data = default;
        bool hasData = false;
        ArrayBufferWriter<byte>? joinedData = null;
        while (!body.IsEmpty)
        {
            var end = body.IndexOfAny((byte)'\r', (byte)'\n');
            if (end < 0)
                break; // A cut inside the final event cannot be mistaken for a complete one.

            var line = body[..end];
            var separatorLength = body[end] == '\r' && end + 1 < body.Length && body[end + 1] == '\n' ? 2 : 1;
            body = body[(end + separatorLength)..];
            if (line.IsEmpty)
            {
                if (hasData)
                {
                    var model = ReadJson(joinedData is null ? data : joinedData.WrittenSpan, eventName);
                    if (model is not null)
                        return model;
                }
                eventName = "";
                data = default;
                hasData = false;
                joinedData = null;
                continue;
            }

            var colon = line.IndexOf((byte)':');
            if (colon < 0)
                colon = line.Length;
            var field = line[..colon];
            var value = colon < line.Length ? line[(colon + 1)..] : default;
            if (value.StartsWith(" "u8))
                value = value[1..];

            if (field.SequenceEqual("event"u8))
                eventName = Encoding.UTF8.GetString(value);
            else if (field.SequenceEqual("data"u8))
            {
                if (!hasData)
                    data = value;
                else
                {
                    if (joinedData is null)
                    {
                        joinedData = new ArrayBufferWriter<byte>();
                        joinedData.Write(data);
                    }
                    joinedData.Write("\n"u8);
                    joinedData.Write(value);
                }
                hasData = true;
            }
        }
        return null;
    }

    private static string? ReadJson(ReadOnlySpan<byte> body, string? eventName)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            string? model = null, type = null, objectType = null, messageModel = null, responseModel = null;
            bool hasError = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("model"u8))
                    model = ReadString(ref reader);
                else if (reader.ValueTextEquals("type"u8))
                    type = ReadString(ref reader);
                else if (reader.ValueTextEquals("object"u8))
                    objectType = ReadString(ref reader);
                else if (reader.ValueTextEquals("error"u8))
                    hasError |= ReadError(ref reader);
                else if (reader.ValueTextEquals("message"u8))
                    messageModel = ReadNestedModel(ref reader);
                else if (reader.ValueTextEquals("response"u8))
                    responseModel = ReadNestedModel(ref reader);
                else
                    SkipValue(ref reader);
            }
            // Reject incomplete JSON and trailing garbage even if model appeared before the fault.
            reader.Read();
            if (hasError || type == "error")
                return null;

            if (eventName is null)
                return model;

            return (type ?? eventName) switch
            {
                "message_start" => messageModel,
                "response.created" or "response.completed" => responseModel,
                "error" => null,
                // Chat Completions uses unnamed events with a top-level model on each chunk.
                _ => objectType == "chat.completion.chunk" ? model : null
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A malformed event (including an invalid UTF-8 model) must never cost the log a row.
            return null;
        }
    }

    private static string? ReadNestedModel(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? model = null, type = null;
        bool hasError = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("model"u8))
                model = ReadString(ref reader);
            else if (reader.ValueTextEquals("type"u8))
                type = ReadString(ref reader);
            else if (reader.ValueTextEquals("error"u8))
                hasError |= ReadError(ref reader);
            else
                SkipValue(ref reader);
        }
        return hasError || type == "error" ? null : model;
    }

    private static string? ReadString(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        reader.Skip();
        return null;
    }

    private static bool ReadError(ref Utf8JsonReader reader)
    {
        reader.Read();
        // Successful Responses JSON includes error:null.
        var hasError = reader.TokenType != JsonTokenType.Null;
        reader.Skip();
        return hasError;
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        reader.Read();
        reader.Skip();
    }
}
