using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;

namespace VibeRails.Data.Sqlite;

public sealed partial class LaunchStore
{
    public async Task<IReadOnlyList<LaunchPreset>> ListPresetsAsync(CancellationToken ct = default)
        => await ListAsync("LaunchPresets", StorageJsonSerializerContext.Default.LaunchPreset, ct);
    public async Task<IReadOnlyList<LaunchAction>> ListActionsAsync(CancellationToken ct = default)
        => await ListAsync("Actions", StorageJsonSerializerContext.Default.LaunchAction, ct);
    private async Task<IReadOnlyList<T>> ListAsync<T>(string table, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var cmd = Command(db, $"SELECT Payload FROM {table} ORDER BY Name, Id;", tx);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<T> results = [];
        while (await reader.ReadAsync(ct)) results.Add(JsonSerializer.Deserialize(reader.GetString(0), type)!);
        reader.Close();
        for (var i = 0; i < results.Count; i++) results[i] = await WithScope(db, tx, results[i], ct);
        return results;
    }

    public async Task<LaunchPreset?> GetPresetAsync(string id, CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        return await ReadDefinition(db, tx, "LaunchPresets", id, StorageJsonSerializerContext.Default.LaunchPreset, ct);
    }
    public async Task<LaunchAction?> GetActionAsync(string id, CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        return await ReadDefinition(db, tx, "Actions", id, StorageJsonSerializerContext.Default.LaunchAction, ct);
    }
    private static async Task<T?> ReadDefinition<T>(SqliteConnection db, SqliteTransaction? tx, string table,
        string id, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var cmd = Command(db, $"SELECT Payload FROM {table} WHERE Id=$id;", tx, ("$id", id));
        return await cmd.ExecuteScalarAsync(ct) is string json
            ? await WithScope(db, tx, JsonSerializer.Deserialize(json, type)!, ct) : default;
    }

    private static async Task<T> WithScope<T>(SqliteConnection db, SqliteTransaction? tx, T value, CancellationToken ct)
    {
        return value switch
        {
            LaunchPreset p => (T)(object)(p with { ProjectPaths = await ReadScope(db, tx, "preset", p.Id, ct) }),
            LaunchAction a => (T)(object)(a with { ProjectPaths = await ReadScope(db, tx, "action", a.Id, ct) }),
            _ => value
        };
    }

    private static async Task<string[]> ReadScope(SqliteConnection db, SqliteTransaction? tx, string type, string id, CancellationToken ct)
    {
        using var cmd = Command(db, "SELECT ProjectKey FROM DefinitionProjects WHERE DefinitionType=$type AND DefinitionId=$id ORDER BY ProjectKey;",
            tx, ("$type", type), ("$id", id));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<string> paths = [];
        while (await reader.ReadAsync(ct)) paths.Add(reader.GetString(0));
        return paths.ToArray();
    }

    private static async Task SaveScope(SqliteConnection db, SqliteTransaction tx, string type, string id, string[] paths, CancellationToken ct)
    {
        using var clear = Command(db, "DELETE FROM DefinitionProjects WHERE DefinitionType=$type AND DefinitionId=$id;", tx, ("$type", type), ("$id", id));
        await clear.ExecuteNonQueryAsync(ct);
        foreach (var path in paths)
        {
            using var insert = Command(db, "INSERT INTO DefinitionProjects(DefinitionType,DefinitionId,ProjectKey) VALUES($type,$id,$path);",
                tx, ("$type", type), ("$id", id), ("$path", path));
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<LaunchPreset> SavePresetAsync(LaunchPreset preset, long expectedRevision, CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = await ReadDefinition(db, tx, "LaunchPresets", preset.Id, StorageJsonSerializerContext.Default.LaunchPreset, ct);
        RequireRevision(current?.Revision, expectedRevision);
        if (current?.RetiredUtc is not null) throw new LaunchConflictException("This preset is retired. Create a new preset.", current.Revision);
        var now = DateTime.UtcNow;
        preset = preset with { Revision = expectedRevision + 1, CreatedUtc = current?.CreatedUtc ?? now, UpdatedUtc = now,
            ProjectPaths = LaunchProjectScope.Normalize(preset.ProjectPaths) ?? current?.ProjectPaths ?? [] };
        using var cmd = Command(db, """
            INSERT INTO LaunchPresets(Id,Name,Description,Cli,InitialPrompt,Enabled,Revision,CreatedUTC,UpdatedUTC,RetiredUTC,Payload)
            VALUES($id,$name,$description,$cli,$prompt,$enabled,$revision,$created,$updated,$retired,$payload)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Description=excluded.Description,Cli=excluded.Cli,
            InitialPrompt=excluded.InitialPrompt,Enabled=excluded.Enabled,Revision=excluded.Revision,
            UpdatedUTC=excluded.UpdatedUTC,RetiredUTC=excluded.RetiredUTC,Payload=excluded.Payload;
            """, tx, ("$id", preset.Id), ("$name", preset.Name), ("$description", preset.Description),
            ("$cli", (int)preset.Cli), ("$prompt", preset.InitialPrompt), ("$enabled", preset.Enabled),
            ("$revision", preset.Revision), ("$created", Stamp(preset.CreatedUtc)), ("$updated", Stamp(now)),
            ("$retired", preset.RetiredUtc is { } retired ? Stamp(retired) : null),
            ("$payload", JsonSerializer.Serialize(preset with { ProjectPaths = null }, StorageJsonSerializerContext.Default.LaunchPreset)));
        await cmd.ExecuteNonQueryAsync(ct);
        await SaveScope(db, tx, "preset", preset.Id, preset.ProjectPaths, ct);
        tx.Commit();
        return preset;
    }

    public Task<LaunchAction> SaveActionAsync(LaunchAction action, long expectedRevision, CancellationToken ct = default) =>
        SaveApprovedActionAsync(action, expectedRevision, "", null, ct);

    public async Task<LaunchAction> SaveApprovedActionAsync(LaunchAction action, long expectedRevision, string project, string? hash, CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = await ReadDefinition(db, tx, "Actions", action.Id, StorageJsonSerializerContext.Default.LaunchAction, ct);
        RequireRevision(current?.Revision, expectedRevision);
        if (current?.RetiredUtc is not null) throw new LaunchConflictException("This Action is retired. Create a new Action.", current.Revision);
        var now = DateTime.UtcNow;
        action = action with { Revision = expectedRevision + 1, CreatedUtc = current?.CreatedUtc ?? now, UpdatedUtc = now,
            ProjectPaths = LaunchProjectScope.Normalize(action.ProjectPaths) ?? current?.ProjectPaths ?? [] };
        using var cmd = Command(db, """
            INSERT INTO Actions(Id,Name,Description,Kind,Enabled,Revision,CreatedUTC,UpdatedUTC,RetiredUTC,Payload)
            VALUES($id,$name,$description,$kind,$enabled,$revision,$created,$updated,$retired,$payload)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Description=excluded.Description,Kind=excluded.Kind,
            Enabled=excluded.Enabled,Revision=excluded.Revision,UpdatedUTC=excluded.UpdatedUTC,
            RetiredUTC=excluded.RetiredUTC,Payload=excluded.Payload;
            """, tx, ("$id", action.Id), ("$name", action.Name), ("$description", action.Description),
            ("$kind", action.Kind), ("$enabled", action.Enabled), ("$revision", action.Revision),
            ("$created", Stamp(action.CreatedUtc)), ("$updated", Stamp(now)),
            ("$retired", action.RetiredUtc is { } retired ? Stamp(retired) : null),
            ("$payload", JsonSerializer.Serialize(action with { ProjectPaths = null }, StorageJsonSerializerContext.Default.LaunchAction)));
        await cmd.ExecuteNonQueryAsync(ct);
        await SaveScope(db, tx, "action", action.Id, action.ProjectPaths, ct);
        if (hash is not null)
        {
            using var approval = Command(db, "INSERT OR IGNORE INTO ScriptApprovals VALUES($project,$id,$hash,$now);", tx,
                ("$project", project), ("$id", action.Id), ("$hash", hash), ("$now", Stamp(now)));
            await approval.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return action;
    }

    private static void RequireRevision(long? current, long expected)
    {
        if ((current ?? 0) != expected || expected < 0)
            throw new LaunchConflictException("This definition changed. Reload it and apply your changes to the current revision.", current ?? 0);
    }

    public async Task<IReadOnlyList<LaunchTriggerBinding>> ListBindingsAsync(string? projectPath = null,
        string? definitionId = null, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT Payload,NextRunUTC FROM TriggerBindings WHERE ($project IS NULL OR ProjectPath=$project)
                AND ($definition IS NULL OR DefinitionId=$definition) ORDER BY Id;
            """, null, ("$project", projectPath), ("$definition", definitionId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<LaunchTriggerBinding> results = [];
        while (await reader.ReadAsync(ct)) results.Add(JsonSerializer.Deserialize(reader.GetString(0),
            StorageJsonSerializerContext.Default.LaunchTriggerBinding)! with
            { NextRunUtc = reader.IsDBNull(1) ? null : Date(reader.GetString(1)) });
        return results;
    }

    public async Task<LaunchTriggerBinding> SaveBindingAsync(LaunchTriggerBinding binding, long expectedRevision,
        CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = await ReadDefinition(db, tx, "TriggerBindings", binding.Id, StorageJsonSerializerContext.Default.LaunchTriggerBinding, ct);
        RequireRevision(current?.Revision, expectedRevision);
        if (current is not null && current.ProjectPath != binding.ProjectPath)
            throw new LaunchValidationException("A binding belongs to its original project.");
        if (binding.Enabled) await RequireAvailable(db, tx, binding.DefinitionType, binding.DefinitionId, null, binding.ProjectPath, automatic: true, ct);
        var now = DateTime.UtcNow;
        binding = binding with { Revision = expectedRevision + 1, CreatedUtc = current?.CreatedUtc ?? now, UpdatedUtc = now };
        using var cmd = Command(db, """
            INSERT INTO TriggerBindings(Id,DefinitionId,DefinitionType,ProjectPath,Enabled,Revision,NextRunUTC,Payload)
            VALUES($id,$definition,$type,$project,$enabled,$revision,$next,$payload)
            ON CONFLICT(Id) DO UPDATE SET DefinitionId=excluded.DefinitionId,DefinitionType=excluded.DefinitionType,
            Enabled=excluded.Enabled,Revision=excluded.Revision,NextRunUTC=excluded.NextRunUTC,Payload=excluded.Payload;
            """, tx, ("$id", binding.Id), ("$definition", binding.DefinitionId), ("$type", binding.DefinitionType),
            ("$project", binding.ProjectPath), ("$enabled", binding.Enabled), ("$revision", binding.Revision),
            ("$next", binding.NextRunUtc is { } next ? Stamp(next) : null),
            ("$payload", JsonSerializer.Serialize(binding, StorageJsonSerializerContext.Default.LaunchTriggerBinding)));
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        return binding;
    }

    public async Task<bool> AdvanceBindingAsync(string id, long revision, DateTime occurrenceUtc, DateTime nextUtc, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE TriggerBindings SET NextRunUTC=$next WHERE Id=$id AND Revision=$revision AND NextRunUTC=$expected;",
            null, ("$id", id), ("$revision", revision), ("$expected", Stamp(occurrenceUtc)), ("$next", Stamp(nextUtc)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task RequireAvailable(SqliteConnection db, SqliteTransaction tx, string type, string id,
        long? revision, string project, bool automatic, CancellationToken ct)
    {
        var table = type switch { "preset" => "LaunchPresets", "action" => "Actions", _ => throw new LaunchValidationException("Unknown definition type.") };
        using var cmd = Command(db, $"SELECT Revision,Enabled,RetiredUTC{(type == "preset" ? ",InitialPrompt" : "")} FROM {table} WHERE Id=$id;", tx, ("$id", id));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(1) || !reader.IsDBNull(2))
            throw new LaunchValidationException("This definition is unavailable or retired. Choose an enabled definition.");
        if (revision is { } expected && reader.GetInt64(0) != expected)
            throw new LaunchConflictException("This definition changed before enqueue. Run again to use its current revision.", reader.GetInt64(0));
        if (automatic && type == "preset" && (reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3))))
            throw new LaunchValidationException("A Quick Launch cannot run automatically. Add an initial prompt or choose an Agent.");
        reader.Close();
        LaunchProjectScope.Require(await ReadScope(db, tx, type, id, ct), project);
    }

    public async Task ApproveScriptAsync(string projectPath, string definitionId, string hash, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "INSERT OR IGNORE INTO ScriptApprovals VALUES($project,$definition,$hash,$now);", null,
            ("$project", projectPath), ("$definition", definitionId), ("$hash", hash), ("$now", Stamp(DateTime.UtcNow)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> IsScriptApprovedAsync(string projectPath, string definitionId, string hash, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT 1 FROM ScriptApprovals WHERE ProjectPath=$project AND DefinitionId=$definition AND ContentHash=$hash;", null,
            ("$project", projectPath), ("$definition", definitionId), ("$hash", hash));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
