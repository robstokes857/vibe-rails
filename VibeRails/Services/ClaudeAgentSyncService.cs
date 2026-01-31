namespace VibeRails.Services;

public interface IClaudeAgentSyncService
{
    /// <summary>
    /// Called when a Claude session starts. Finds CLAUDE.md files in the working directory
    /// and syncs content to the matching AGENTS.md files.
    /// </summary>
    Task OnSessionStartAsync(string sessionId, string workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a Claude session ends. Updates AGENTS.md files with any changes.
    /// </summary>
    Task OnSessionEndAsync(string sessionId, string workingDirectory, CancellationToken cancellationToken);
}

public class ClaudeAgentSyncService : IClaudeAgentSyncService
{
    private const string SYNC_START_MARKER = "<!-- Synced from CLAUDE.md on ";
    private const string SYNC_END_MARKER = "<!-- End CLAUDE.md sync -->";

    public async Task OnSessionStartAsync(string sessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var claudeMdFiles = FindClaudeMdFiles(workingDirectory);

            foreach (var claudeMdPath in claudeMdFiles)
            {
                var agentsMdPath = GetMatchingAgentsMdPath(claudeMdPath);
                await SyncClaudeToAgentsAsync(claudeMdPath, agentsMdPath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the session
            Console.Error.WriteLine($"[ClaudeAgentSync] Error during session start sync: {ex.Message}");
        }
    }

    public async Task OnSessionEndAsync(string sessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var claudeMdFiles = FindClaudeMdFiles(workingDirectory);

            foreach (var claudeMdPath in claudeMdFiles)
            {
                var agentsMdPath = GetMatchingAgentsMdPath(claudeMdPath);
                await SyncClaudeToAgentsAsync(claudeMdPath, agentsMdPath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the session
            Console.Error.WriteLine($"[ClaudeAgentSync] Error during session end sync: {ex.Message}");
        }
    }

    /// <summary>
    /// Find all CLAUDE.md files in the directory tree (case-insensitive).
    /// </summary>
    private static List<string> FindClaudeMdFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return new List<string>();

        return Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFullPath)
            .ToList();
    }

    /// <summary>
    /// Get the path to the AGENTS.md file that corresponds to a CLAUDE.md file.
    /// They should be in the same directory.
    /// </summary>
    private static string GetMatchingAgentsMdPath(string claudeMdPath)
    {
        var directory = Path.GetDirectoryName(claudeMdPath) ?? ".";
        return Path.Combine(directory, "AGENTS.md");
    }

    /// <summary>
    /// Sync content from CLAUDE.md to AGENTS.md.
    /// If AGENTS.md doesn't exist, create it.
    /// If it exists, update the synced section while preserving other content.
    /// </summary>
    private async Task SyncClaudeToAgentsAsync(string claudeMdPath, string agentsMdPath, CancellationToken cancellationToken)
    {
        // Read CLAUDE.md content
        if (!File.Exists(claudeMdPath))
            return;

        var claudeContent = await File.ReadAllTextAsync(claudeMdPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(claudeContent))
            return;

        var timestamp = DateTime.UtcNow.ToString("O");
        var syncedSection = $"{SYNC_START_MARKER}{timestamp} -->\n{claudeContent.Trim()}\n{SYNC_END_MARKER}";

        if (File.Exists(agentsMdPath))
        {
            // Update existing AGENTS.md
            var existingContent = await File.ReadAllTextAsync(agentsMdPath, cancellationToken);
            var updatedContent = ReplaceSyncedSection(existingContent, syncedSection);
            await File.WriteAllTextAsync(agentsMdPath, updatedContent, cancellationToken);
        }
        else
        {
            // Create new AGENTS.md with header and synced content
            var newContent = $"# Repository Guidelines\n\nThis file contains instructions for AI agents working on this codebase.\n\n{syncedSection}\n";
            await File.WriteAllTextAsync(agentsMdPath, newContent, cancellationToken);
        }
    }

    /// <summary>
    /// Replace the synced section in existing content, or append if not found.
    /// </summary>
    private static string ReplaceSyncedSection(string existingContent, string newSyncedSection)
    {
        // Find existing synced section
        var startIndex = existingContent.IndexOf(SYNC_START_MARKER, StringComparison.Ordinal);
        var endIndex = existingContent.IndexOf(SYNC_END_MARKER, StringComparison.Ordinal);

        if (startIndex >= 0 && endIndex > startIndex)
        {
            // Replace existing section
            var before = existingContent.Substring(0, startIndex);
            var after = existingContent.Substring(endIndex + SYNC_END_MARKER.Length);
            return before + newSyncedSection + after;
        }
        else
        {
            // Append new section at the end
            var trimmed = existingContent.TrimEnd();
            return trimmed + "\n\n" + newSyncedSection + "\n";
        }
    }
}
