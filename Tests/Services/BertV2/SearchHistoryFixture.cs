using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;

namespace Tests.Services.BertV2;

/// <summary>Canonical history plus explicit background population, never query-driven indexing.</summary>
internal sealed class SearchHistoryFixture
{
    private readonly string state;
    private readonly IBoardStore board;
    private readonly IBertV2BgeEmbedder model;
    internal SqliteSearchIndexStore Index { get; }
    internal BertSearchDbService Search { get; }

    internal SearchHistoryFixture(string directory, IBertV2BgeEmbedder model)
    {
        Directory.CreateDirectory(directory);
        state = Path.Combine(directory, "state.db");
        StateDatabaseSchema.Ensure($"Data Source={state}");
        board = SqliteStorage.CreateBoardStore(state);
        this.model = model;
        Index = new(Path.Combine(directory, "search.db"), state);
        Search = new(Index);
    }

    internal void Capture(string session, long id, string text)
    {
        using var db = new SqliteConnection($"Data Source={state}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Sessions(Id,Cli,WorkingDirectory,StartedUTC) VALUES($session,'codex','test',$utc);
            INSERT INTO UserInputs(Id,SessionId,Sequence,InputText,TimestampUTC,SearchComponent) VALUES($id,$session,$id,$text,$utc,'search/1')
                ON CONFLICT(Id) DO UPDATE SET InputText=excluded.InputText;
            """;
        cmd.Parameters.AddWithValue("$session", session);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Refresh();
    }

    internal void Refresh()
    {
        Index.ReconcileAsync(board, 25, Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        SearchTestIndex.Embed(Index, model);
    }
}
