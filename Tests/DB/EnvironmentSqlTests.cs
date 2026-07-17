using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services;
using Xunit;

namespace Tests.DB;

public class EnvironmentSqlTests
{
    [Fact]
    public async Task SelectCustomEnvironments_IncludesProviderNamedEnvironmentWithPopulatedPath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = SqlStrings.CreateEnvironmentsTable;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertEnvironmentAsync(
            connection,
            customName: "OpenCode",
            llm: LLM.OpenCode,
            path: Path.Combine(Path.GetTempPath(), "viberails-envs", "OpenCode"),
            cancellationToken);
        await InsertEnvironmentAsync(
            connection,
            customName: "Copilot",
            llm: LLM.Copilot,
            path: "",
            cancellationToken);

        await using var select = connection.CreateCommand();
        select.CommandText = SqlStrings.SelectCustomEnvironments;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("OpenCode", reader.GetString(1));
        Assert.Equal(LLM.OpenCode, (LLM)reader.GetInt32(2));
        Assert.False(await reader.ReadAsync(cancellationToken));
    }

    private static async Task InsertEnvironmentAsync(
        SqliteConnection connection,
        string customName,
        LLM llm,
        string path,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText = SqlStrings.InsertEnvironment;
        insert.Parameters.AddWithValue("$customName", customName);
        insert.Parameters.AddWithValue("$llm", (int)llm);
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$customArgs", "");
        insert.Parameters.AddWithValue("$customPrompt", "");
        insert.Parameters.AddWithValue("$createdUTC", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$lastUsedUTC", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteScalarAsync(cancellationToken);
    }
}
