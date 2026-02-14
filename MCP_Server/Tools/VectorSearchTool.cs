using System.ComponentModel;
using System.Text;
using System.Text.Json;
using MCP_Server.Models;
using MCP_Server.Services;
using ModelContextProtocol.Server;
using Serilog;

namespace MCP_Server.Tools;

[McpServerToolType]
public class VectorSearchTool
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".vibe_rails", "vector");

    private static SimpleVectorDb? _userTermsDb;
    private static SimpleVectorDb? _conversationDb;
    private static readonly List<UserTermEntry> _userTermEntries = new();
    private static readonly List<ConversationHistoryEntry> _conversationEntries = new();
    private static readonly object _lock = new();
    private static bool _initialized;

    private static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            _userTermsDb = new SimpleVectorDb();
            _conversationDb = new SimpleVectorDb();

            Directory.CreateDirectory(StoragePath);
            LoadExistingData();
            _initialized = true;
        }
    }

    private static void LoadExistingData()
    {
        var userTermsPath = Path.Combine(StoragePath, "user_terms.jsonl");
        if (File.Exists(userTermsPath))
        {
            foreach (var line in File.ReadLines(userTermsPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize(line.Trim(), VectorJsonContext.Default.UserTermEntry);
                    if (entry != null)
                    {
                        _userTermEntries.Add(entry);
                        var text = string.IsNullOrEmpty(entry.Description)
                            ? $"{entry.UserTerm}: {entry.TargetPath}"
                            : $"{entry.UserTerm}: {entry.Description} ({entry.TargetPath})";
                        _userTermsDb!.AddText(text, entry.Id);
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to parse JSONL line"); }
            }
        }

        var conversationPath = Path.Combine(StoragePath, "conversation_history.jsonl");
        if (File.Exists(conversationPath))
        {
            foreach (var line in File.ReadLines(conversationPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize(line.Trim(), VectorJsonContext.Default.ConversationHistoryEntry);
                    if (entry != null)
                    {
                        _conversationEntries.Add(entry);
                        _conversationDb!.AddText(entry.Content, entry.Id);
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to parse JSONL line"); }
            }
        }
    }

    [McpServerTool]
    [Description("Search for code files using user terminology. Use this FIRST before searching the codebase to find relevant files based on how users typically refer to them.")]
    public static string SearchUserTerms(
        [Description("The search query - can be informal terminology like 'the repo class' or 'database helper'")] string query,
        [Description("Maximum number of results to return (default: 5)")] int maxResults = 5)
    {
        EnsureInitialized();

        var results = _userTermsDb!.Search(query, pageCount: maxResults);

        if (results?.Texts == null || !results.Texts.Any())
        {
            return "No matching terms found. Consider adding a mapping with AddUserTermMapping when the user refers to code informally.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Found mappings:");
        foreach (var item in results.Texts)
        {
            var id = item.Metadata ?? "";
            UserTermEntry? entry;
            lock (_lock)
            {
                entry = _userTermEntries.FirstOrDefault(e => e.Id == id);
            }
            var target = entry?.TargetPath ?? "unknown";
            sb.AppendLine($"- \"{entry?.UserTerm ?? item.Text}\" -> {target}");
            if (!string.IsNullOrEmpty(entry?.Description))
            {
                sb.AppendLine($"  Description: {entry.Description}");
            }
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("Add a mapping between user terminology and a code file path. Use this when you notice the user refers to code using informal terms so future searches can find it.")]
    public static string AddUserTermMapping(
        [Description("The informal term the user uses (e.g., 'the repo', 'data helper', 'main class')")] string userTerm,
        [Description("The actual file path or class name this refers to (e.g., 'src/repository/db_repository.rs', 'DataHelper.cs')")] string targetPath,
        [Description("Optional description providing context about this mapping")] string? description = null)
    {
        EnsureInitialized();

        var id = Guid.NewGuid().ToString();
        var entry = new UserTermEntry
        {
            Id = id,
            UserTerm = userTerm,
            TargetPath = targetPath,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _userTermEntries.Add(entry);
        }

        var text = string.IsNullOrEmpty(description)
            ? $"{userTerm}: {targetPath}"
            : $"{userTerm}: {description} ({targetPath})";
        _userTermsDb!.AddText(text, id);

        // Persist to disk (fire-and-forget)
        Task.Run(() => PersistUserTermAsync(entry));

        return $"Added mapping: '{userTerm}' -> '{targetPath}'";
    }

    [McpServerTool]
    [Description("Search conversation history for relevant past interactions. Use this to find context from previous discussions.")]
    public static string SearchConversations(
        [Description("The search query to find relevant past conversations")] string query,
        [Description("Maximum number of results to return (default: 5)")] int maxResults = 5)
    {
        EnsureInitialized();

        var results = _conversationDb!.Search(query, pageCount: maxResults);

        if (results?.Texts == null || !results.Texts.Any())
        {
            return "No matching conversations found.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Relevant past interactions:");
        foreach (var item in results.Texts)
        {
            var id = item.Metadata ?? "";
            ConversationHistoryEntry? entry;
            lock (_lock)
            {
                entry = _conversationEntries.FirstOrDefault(e => e.Id == id);
            }
            var role = entry?.Role ?? "unknown";
            var timestamp = entry?.Timestamp.ToString("g") ?? "";
            var preview = (item.Text?.Length ?? 0) > 200 ? item.Text![..197] + "..." : item.Text ?? "";
            sb.AppendLine($"[{role}] ({timestamp}): {preview}");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("Store a conversation entry for future retrieval. Call this to save important user requests or assistant responses.")]
    public static string AddConversationEntry(
        [Description("The role: 'user', 'assistant', or 'system'")] string role,
        [Description("The message content to store")] string content,
        [Description("Optional: The project path this conversation relates to")] string? projectPath = null)
    {
        EnsureInitialized();

        var id = Guid.NewGuid().ToString();
        var entry = new ConversationHistoryEntry
        {
            Id = id,
            Role = role,
            Content = content,
            ProjectPath = projectPath,
            Timestamp = DateTime.UtcNow
        };

        lock (_lock)
        {
            _conversationEntries.Add(entry);
        }

        _conversationDb!.AddText(content, id);

        // Persist to disk (fire-and-forget)
        Task.Run(() => PersistConversationAsync(entry));

        return "Conversation entry stored.";
    }

    [McpServerTool]
    [Description("Get statistics about the vector index.")]
    public static string GetVectorStats()
    {
        EnsureInitialized();

        int termCount, convCount;
        lock (_lock)
        {
            termCount = _userTermEntries.Count;
            convCount = _conversationEntries.Count;
        }

        return $"Vector Index Statistics:\n- User term mappings: {termCount}\n- Conversation entries: {convCount}\n- Storage path: {StoragePath}";
    }

    private static async Task PersistUserTermAsync(UserTermEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, VectorJsonContext.Default.UserTermEntry);
        await AppendToFileWithRetryAsync(Path.Combine(StoragePath, "user_terms.jsonl"), json);
    }

    private static async Task PersistConversationAsync(ConversationHistoryEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, VectorJsonContext.Default.ConversationHistoryEntry);
        await AppendToFileWithRetryAsync(Path.Combine(StoragePath, "conversation_history.jsonl"), json);
    }

    private static async Task AppendToFileWithRetryAsync(string path, string content, int maxRetries = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(fs);
                await writer.WriteLineAsync(content);
                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                Log.Warning(ex, "Retry {Attempt} writing to {Path}", i + 1, path);
                await Task.Delay(100 * (i + 1));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to write to {Path} after {MaxRetries} attempts", path, maxRetries);
            }
        }
    }
}
