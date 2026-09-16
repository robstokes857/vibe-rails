using Microsoft.Data.Sqlite;
using VibeRails.Services.UserInOut;

namespace VibeRails.Data.Sqlite;

/// <summary>Stores precisely the filtered text indexed by FTS; raw capture remains independent.</summary>
internal static class SearchIndexWriter
{
    internal static void Synchronize(SqliteConnection connection, SqliteTransaction transaction, long inputId, string? rawText)
    {
        var safe = InputEtlFilter.Process(rawText);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", inputId);
        if (string.IsNullOrWhiteSpace(safe))
        {
            command.CommandText = "DELETE FROM UserInputSearchDocuments WHERE UserInputId=$id;";
        }
        else
        {
            command.CommandText = """
                INSERT INTO UserInputSearchDocuments(UserInputId,InputText) VALUES ($id,$text)
                ON CONFLICT(UserInputId) DO UPDATE SET InputText=excluded.InputText
                WHERE InputText IS NOT excluded.InputText;
                """;
            command.Parameters.AddWithValue("$text", safe);
        }
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM UserInputSearchPending WHERE UserInputId=$id;";
        command.ExecuteNonQuery();
    }
}
