using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static void ValidateAgentWorkflow(string workflowMode, string? managerSelection)
    {
        if (!BoardWorkflowModes.IsValid(workflowMode))
            throw new BoardValidationException("Workflow must be manual or agent.");
        if (workflowMode == BoardWorkflowModes.Agent && string.IsNullOrWhiteSpace(managerSelection))
            throw new BoardValidationException("Select a manager agent for the Agent workflow.");
    }

    private static void AddManagerParameters(SqliteCommand command, string workflowMode, string? managerSelection,
        BaseLlmOptions? managerOptions)
    {
        command.Parameters.AddWithValue("$workflow", workflowMode);
        command.Parameters.AddWithValue("$manager", (object?)managerSelection ?? DBNull.Value);
        command.Parameters.AddWithValue("$managerOptions", managerOptions is null ? DBNull.Value
            : JsonSerializer.Serialize(managerOptions, StorageJsonSerializerContext.Default.BaseLlmOptions));
    }

    // Launch configuration is local, just like the worker's BaseLlmOptions. Recording it with
    // RemoteSeq=0 keeps it reviewable without expanding the hosted Board sync field protocol.
    private static Task LogAgentWorkflowChangedAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord before, BoardCardRecord after, BoardAuthor author, CancellationToken cancellationToken)
    {
        var log = new CardLogChanges();
        log.AgentWorkflow(before, after);
        return log.IsEmpty ? Task.CompletedTask
            : InsertLogEntryAsync(connection, transaction, after.Id, author, BoardCommentKinds.Change,
                log.Summary, log.ToJson(), after.UpdatedUtc, cancellationToken, localOnly: true);
    }
}
