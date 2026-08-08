using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
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

    [Fact]
    public async Task WorkspaceColumns_RoundTripThroughInsertAndSelect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = SqlStrings.CreateEnvironmentsTable;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        const string projectPath = @"C:\source\app";
        await InsertEnvironmentAsync(
            connection,
            customName: "Nightly Review",
            llm: LLM.Claude,
            path: "",
            cancellationToken,
            workspaceMode: EnvironmentWorkspaceMode.PerRun,
            projectPath: projectPath);

        await using var select = connection.CreateCommand();
        select.CommandText = SqlStrings.SelectEnvironmentByName;
        select.Parameters.AddWithValue("$customName", "Nightly Review");
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken));
        // Ordinals matter: Repository.ReadEnvironment reads these positionally, so a column
        // added in the wrong place in one statement silently misreads every environment.
        Assert.Equal((int)EnvironmentWorkspaceMode.PerRun, reader.GetInt32(10));
        Assert.Equal(projectPath, reader.GetString(11));
    }

    [Fact]
    public async Task WorkspaceColumns_DefaultToProjectScopedNowhere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = SqlStrings.CreateEnvironmentsTable;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertEnvironmentAsync(connection, "Legacy", LLM.Claude, "", cancellationToken);

        await using var select = connection.CreateCommand();
        select.CommandText = SqlStrings.SelectEnvironmentByName;
        select.Parameters.AddWithValue("$customName", "Legacy");
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal((int)EnvironmentWorkspaceMode.Project, reader.GetInt32(10));
        // Null scope is the no-backfill state: visible in every project.
        Assert.True(reader.IsDBNull(11));
    }

    private static async Task InsertEnvironmentAsync(
        SqliteConnection connection,
        string customName,
        LLM llm,
        string path,
        CancellationToken cancellationToken,
        EnvironmentWorkspaceMode workspaceMode = EnvironmentWorkspaceMode.Project,
        string? projectPath = null)
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
        insert.Parameters.AddWithValue("$hidden", 0);
        insert.Parameters.AddWithValue("$automationWorker", 0);
        insert.Parameters.AddWithValue("$workspaceMode", (int)workspaceMode);
        insert.Parameters.AddWithValue("$projectPath", (object?)projectPath ?? DBNull.Value);
        await insert.ExecuteScalarAsync(cancellationToken);
    }
}
