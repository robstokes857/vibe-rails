using System.Text;
using System.Text.Json;
using VibeRails.Data.Replay;

namespace VibeRails.Data.Sqlite.Replay;

/// <summary>Reads bounded tool summaries from emitted responses, excluding repeated request history.</summary>
public static class ToolParser
{
    internal const int MaxResponseCharacters = 2_000_000;
    internal const int MaxTools = 64;
    internal const int MaxLabelCharacters = 256;
    internal const int MaxArgumentCharacters = 8_192;
    internal const int MaxTotalArgumentCharacters = 65_536;

    public sealed record Parsed(List<ToolCall> Tools, string? Note);

    /// <summary>Malformed events and display truncation are reported without losing other events.</summary>
    public static Parsed Parse(string body, bool inputTruncated = false)
    {
        var parser = new Parser { truncated = inputTruncated || body.Length > MaxResponseCharacters };
        parser.ReadBody(body.Length > MaxResponseCharacters ? body[..MaxResponseCharacters] : body);
        return parser.Result();
    }

    private sealed class Partial
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Arguments = new();
    }

    private sealed class Parser
    {
        private readonly Dictionary<string, ToolCall> calls = [];
        private readonly Dictionary<int, Partial> blocks = [];
        private int argumentBudget = MaxTotalArgumentCharacters;
        private bool malformed;
        private bool recognized;
        private bool hasBody;
        internal bool truncated;

        private bool IsObject(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) return true;
            malformed = true;
            return false;
        }

        private string Clip(string text, int limit)
        {
            if (text.Length <= limit) return text;
            truncated = true;
            // Avoid ending a displayed string between a UTF-16 surrogate pair.
            if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
            return text[..limit];
        }

        private string Text(JsonElement root, string key, bool allowJson = false)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return "";
            if (value.ValueKind == JsonValueKind.String)
            {
                try { return value.GetString() ?? ""; }
                // JsonDocument accepts escaped lone surrogates but decoding them can fail.
                catch (InvalidOperationException) { malformed = true; return ""; }
            }
            if (allowJson) return value.GetRawText();
            malformed = true;
            return "";
        }

        private string Label(JsonElement root, string key) => Clip(Text(root, key), MaxLabelCharacters);

        private string Arguments(string text, int remaining = MaxArgumentCharacters)
        {
            var retained = Clip(text, Math.Min(remaining, argumentBudget));
            argumentBudget -= retained.Length;
            return retained;
        }

        private bool Index(JsonElement root, out int index)
        {
            index = 0;
            if (root.TryGetProperty("index", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out index) && index >= 0) return true;
            malformed = true;
            return false;
        }

        private IEnumerable<JsonElement> Array(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) yield break;
            if (value.ValueKind != JsonValueKind.Array) { malformed = true; yield break; }
            foreach (var item in value.EnumerateArray())
                if (IsObject(item)) yield return item;
        }

        private bool Room()
        {
            if (calls.Count + blocks.Count < MaxTools) return true;
            truncated = true;
            return false;
        }

        private void Add(JsonElement item)
        {
            if (!IsObject(item)) return;
            if (Text(item, "type") is not ("function_call" or "custom_tool_call" or "tool_use" or "server_tool_use" or "function")) return;
            var id = Label(item, "call_id");
            if (id.Length == 0) id = Label(item, "id");
            if (item.TryGetProperty("function", out var function))
            {
                if (!IsObject(function)) return;
                item = function;
            }
            var name = Label(item, "name");
            if (name.Length == 0) { malformed = true; return; }
            if (!calls.ContainsKey(id) && !Room()) return;
            var arguments = Text(item, "arguments", allowJson: true);
            if (arguments.Length == 0) arguments = Text(item, "input", allowJson: true);
            // Missing ids must not duplicate the entire argument payload in a dictionary key.
            if (id.Length == 0)
            {
                var suffix = calls.Count;
                do { id = $"replay-call-{suffix++}"; } while (calls.ContainsKey(id));
            }
            calls[id] = new(id, name, Arguments(arguments));
        }

        private void Read(JsonElement root)
        {
            if (!IsObject(root)) return;
            var type = Text(root, "type");
            if (root.TryGetProperty("item", out var item)) Add(item);
            foreach (var child in Array(root, "output")) Add(child);
            foreach (var child in Array(root, "content")) Add(child);
            if (root.TryGetProperty("response", out var response)) Read(response);

            if (type == "content_block_start")
            {
                if (!root.TryGetProperty("content_block", out var block)) { malformed = true; return; }
                if (!IsObject(block)) return;
                if (Text(block, "type") is "tool_use" or "server_tool_use")
                {
                    if (!Index(root, out var index)) return;
                    if (!blocks.ContainsKey(index) && !Room()) return;
                    var input = Text(block, "input", allowJson: true);
                    var part = new Partial { Id = Label(block, "id"), Name = Label(block, "name") };
                    part.Arguments.Append(Arguments(input == "{}" ? "" : input));
                    blocks[index] = part;
                }
            }
            if (type == "content_block_delta")
            {
                if (!Index(root, out var index)) return;
                if (!root.TryGetProperty("delta", out var delta)) { malformed = true; return; }
                if (!IsObject(delta)) return;
                if (blocks.TryGetValue(index, out var pending))
                    pending.Arguments.Append(Arguments(Text(delta, "partial_json"), MaxArgumentCharacters - pending.Arguments.Length));
            }

            foreach (var choice in Array(root, "choices"))
            {
                if (choice.TryGetProperty("message", out var message) && IsObject(message))
                    foreach (var call in Array(message, "tool_calls")) Add(call);
                if (!choice.TryGetProperty("delta", out var delta) || !IsObject(delta)) continue;
                foreach (var fragment in Array(delta, "tool_calls"))
                {
                    if (!Index(fragment, out var key)) continue;
                    if (!blocks.TryGetValue(key, out var part))
                    {
                        if (!Room()) continue;
                        blocks[key] = part = new();
                    }
                    var callId = Label(fragment, "id");
                    if (callId.Length > 0) part.Id = callId;
                    if (!fragment.TryGetProperty("function", out var function)) continue;
                    if (!IsObject(function)) continue;
                    part.Name += Clip(Text(function, "name"), MaxLabelCharacters - part.Name.Length);
                    part.Arguments.Append(Arguments(Text(function, "arguments"), MaxArgumentCharacters - part.Arguments.Length));
                }
            }
        }

        private void ParseJson(string json)
        {
            try
            {
                // JsonDocument's default depth bound also bounds recursive response wrappers.
                using var document = JsonDocument.Parse(json);
                recognized = true;
                Read(document.RootElement);
            }
            catch (JsonException) { malformed = true; }
        }

        internal void ReadBody(string body)
        {
            hasBody = body.Length > 0;
            var trimmed = body.AsSpan().TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) { ParseJson(body); return; }
            var data = new StringBuilder();
            void Flush()
            {
                if (data.Length == 0) return;
                var json = data.ToString().Trim();
                data.Clear();
                if (json != "[DONE]") ParseJson(json);
            }
            using var lines = new StringReader(body);
            while (lines.ReadLine() is { } line)
            {
                if (line.Length == 0) { Flush(); continue; }
                if (!line.StartsWith("data:")) continue;
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
            Flush();
        }

        internal Parsed Result()
        {
            foreach (var (index, block) in blocks)
            {
                if (block.Name.Length == 0) { malformed = true; continue; }
                var id = block.Id.Length > 0 ? block.Id : $"block-{index}";
                calls[id] = new(id, block.Name, block.Arguments.Length > 0 ? block.Arguments.ToString() : Arguments("{}"));
            }
            List<string> notes = [];
            if (malformed) notes.Add("Some response events have incomplete JSON or invalid structure; those events were skipped.");
            if (!recognized && hasBody) notes.Add("Unrecognized response format; inspect the raw exchange.");
            if (truncated) notes.Add("Tool summary truncated by display limits; inspect the raw exchange for more detail.");
            return new(calls.Values.ToList(), notes.Count > 0 ? string.Join(" ", notes) : null);
        }
    }
}
