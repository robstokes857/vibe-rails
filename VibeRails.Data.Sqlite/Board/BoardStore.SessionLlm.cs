namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<BoardSessionLlm?> FindSessionLlmAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        string? cli = null, environmentName = null;
        int? environmentId = null;
        await using (var board = await OpenAsync(cancellationToken))
        {
            // A Board launch recorded the exact picker key it started (base:claude, env:7:codex).
            await using var command = board.CreateCommand();
            command.CommandText = $"SELECT Selection FROM {AllSessionsSql} WHERE SessionId = $session AND Selection <> '' ORDER BY LinkOrder, CreatedUTC, CardId LIMIT 1;";
            command.Parameters.AddWithValue("$session", sessionId);
            switch ((await command.ExecuteScalarAsync(cancellationToken) as string)?.Trim().Split(':'))
            {
                case ["env", var id, var launched] when int.TryParse(id, out var parsed):
                    environmentId = parsed;
                    cli = launched;
                    break;
                case ["base", var launched]:
                    cli = launched;
                    break;
            }
        }

        await using var state = await OpenStateAsync(cancellationToken);
        // Any other terminal: the CLI and environment name it was opened with.
        if (cli is null && await _stateFeatures.HasTableAsync(state, "Sessions", cancellationToken))
        {
            await using var command = state.CreateCommand();
            command.CommandText = "SELECT Cli, EnvironmentName FROM Sessions WHERE Id = $session LIMIT 1;";
            command.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                cli = reader.IsDBNull(0) ? null : reader.GetString(0);
                environmentName = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)) ? null : reader.GetString(1).Trim();
            }
        }

        var llm = LlmParser.ParseValue(cli);
        if (llm is LLM.NotSet or LLM.Shell)
            return null;

        int? pickable = null;
        if ((environmentId is not null || environmentName is not null)
            && await _stateFeatures.HasTableAsync(state, "Environments", cancellationToken))
        {
            // Only an environment the picker offers. Lane Workers and hidden environments are
            // filtered out of it, so a card assigned to one could not be launched from the Board.
            var offered = (_stateFeatures.HasColumn(state, "Environments", "Hidden") ? " AND Hidden = 0" : "")
                + (_stateFeatures.HasColumn(state, "Environments", "AutomationWorker") ? " AND AutomationWorker = 0" : "");
            await using var command = state.CreateCommand();
            command.CommandText = "SELECT Id FROM Environments WHERE LLM = $llm AND "
                + (environmentId is not null ? "Id = $id" : "CustomName = $name") + offered + " LIMIT 1;";
            command.Parameters.AddWithValue("$llm", (int)llm);
            if (environmentId is not null) command.Parameters.AddWithValue("$id", environmentId.Value);
            else command.Parameters.AddWithValue("$name", environmentName);
            if (await command.ExecuteScalarAsync(cancellationToken) is long id)
                pickable = (int)id;
        }
        return new BoardSessionLlm(LlmParser.ToWireName(llm), pickable);
    }
}
