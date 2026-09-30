using System.Text;
using System.Text.Json;

using VibeRails.Data.Replay;

namespace VibeRails.Data.Sqlite.Replay;

/// <summary>Reads emitted response items only, so repeated request history cannot duplicate calls.</summary>
public static class ToolParser
{
    public sealed record Parsed(List<ToolCall> Tools, string? Note);
    private sealed class Partial
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new();
    }
    public static Parsed Parse(string body)
    {
        Dictionary<string,ToolCall> calls = [];
        Dictionary<int,Partial> blocks = [];
        var malformed = false;
        var recognized = false;
        void Add(JsonElement item)
        {
            var type=Text(item,"type");
            if(type is not ("function_call" or "custom_tool_call" or "tool_use" or "server_tool_use" or "function")) return;
            var id=Text(item,"call_id"); if(id.Length==0) id=Text(item,"id");
            var name=Text(item,"name");
            if(item.TryGetProperty("function",out var function)) { name=Text(function,"name"); item=function; }
            if(name.Length==0) return;
            var arguments=Text(item,"arguments"); if(arguments.Length==0) arguments=Text(item,"input");
            if(id.Length==0) id=name+":"+arguments;
            calls[id]=new(id,name,arguments);
        }
        void Read(JsonElement root)
        {
            if(root.ValueKind!=JsonValueKind.Object) return;
            var type=Text(root,"type");
            if(type.Length>0 || root.TryGetProperty("choices",out _) || root.TryGetProperty("output",out _)) recognized=true;
            if(root.TryGetProperty("item",out var item)) Add(item);
            foreach(var key in new[]{"output","content"})
                if(root.TryGetProperty(key,out var list) && list.ValueKind==JsonValueKind.Array) foreach(var child in list.EnumerateArray()) Add(child);
            if(root.TryGetProperty("response",out var response)) Read(response);
            if(type=="content_block_start" && root.TryGetProperty("content_block",out var block))
            {
                if(Text(block,"type") is "tool_use" or "server_tool_use")
                {
                    var input=Text(block,"input");
                    blocks[root.GetProperty("index").GetInt32()]=new(){Id=Text(block,"id"),Name=Text(block,"name"),Arguments=new(input=="{}"?"":input)};
                }
            }
            if(type=="content_block_delta" && root.TryGetProperty("index",out var index) && blocks.TryGetValue(index.GetInt32(),out var pending))
            {
                if(root.TryGetProperty("delta",out var delta)) pending.Arguments.Append(Text(delta,"partial_json"));
            }
            if(root.TryGetProperty("choices",out var choices) && choices.ValueKind==JsonValueKind.Array)
            {
                foreach(var choice in choices.EnumerateArray())
                {
                    if(choice.TryGetProperty("message",out var message) && message.TryGetProperty("tool_calls",out var completed))
                        foreach(var call in completed.EnumerateArray()) Add(call);
                    if(choice.TryGetProperty("delta",out var delta) && delta.TryGetProperty("tool_calls",out var fragments))
                        foreach(var fragment in fragments.EnumerateArray())
                        {
                            var key=fragment.TryGetProperty("index",out var idx)?idx.GetInt32():0;
                            if(!blocks.TryGetValue(key,out var part)) blocks[key]=part=new();
                            var callId=Text(fragment,"id");if(callId.Length>0) part.Id=callId;
                            if(fragment.TryGetProperty("function",out var f)) { part.Name+=Text(f,"name");part.Arguments.Append(Text(f,"arguments")); }
                        }
                }
            }
        }
        void ParseJson(string json)
        {
            try { using var document=JsonDocument.Parse(json); recognized=true; Read(document.RootElement); }
            catch(JsonException) { malformed=true; }
        }
        if(body.TrimStart().StartsWith('{')) ParseJson(body);
        else
        {
            var data=new StringBuilder();
            void Flush() { if(data.Length==0)return; var json=data.ToString().Trim();data.Clear();if(json!="[DONE]")ParseJson(json); }
            using var lines=new StringReader(body);
            while(lines.ReadLine() is { } line)
            {
                if(line.Length==0) { Flush();continue; }
                if(line.StartsWith("data:")) { if(data.Length>0)data.Append('\n');data.Append(line.AsSpan(5).TrimStart()); }
            }
            Flush();
        }
        foreach(var (index,block) in blocks)
            if(block.Name.Length>0) { var id=block.Id.Length>0?block.Id:$"block-{index}";calls[id]=new(id,block.Name,block.Arguments.Length>0?block.Arguments.ToString():"{}"); }
        return new(calls.Values.ToList(),malformed?"Some response data is incomplete or invalid JSON.":!recognized&&body.Length>0?"Unrecognized response format; inspect the raw exchange.":null);
    }
    private static string Text(JsonElement root,string key) => root.ValueKind==JsonValueKind.Object && root.TryGetProperty(key,out var value) ? value.ValueKind==JsonValueKind.String?value.GetString()??"":value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined?"":value.GetRawText() : "";
}
