using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolClientAuthorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("codex")]
    [InlineData("base:codex")]
    public async Task CreateCard_SnapshotsSavedDefaults_ForExplicitAndCreatorAssignments(string? assignee)
    {
        var saved = await SavePickerDefaultsAsync("codex", new("gpt-6-astra", "xhigh", Yolo: true, Speed: "ultrafast"));
        await LinkLaunchAsync(TerminalSession, "base:codex", "codex");
        _resolver.CurrentSessionId = TerminalSession;
        var client = await ConnectAsync("codex-mcp-client");
        var arguments = new Dictionary<string, object?> { ["title"] = "Saved defaults" };
        if (assignee is not null) arguments["assignee"] = assignee;

        Assert.StartsWith("Created ", await CallAsync(client, "create_board_card", arguments));

        var card = await CreatedCardAsync("Saved defaults");
        Assert.Equal("base:codex", card.Assignee);
        Assert.Equal(saved, card.BaseLlmOptions);
        var read = await CallAsync(client, "get_board_card", new() { ["card"] = card.Key });
        Assert.Contains("gpt-6-astra", read);
        Assert.Contains("ultrafast", read);
        Assert.Equal(BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, saved),
            BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, card.BaseLlmOptions));
    }

    [Fact]
    public async Task AssignmentsLoadCurrentDefaults_WhileUnchangedAssignmentsKeepTheirSnapshot()
    {
        var codex = await SavePickerDefaultsAsync("codex", new("gpt-6-astra", "xhigh", Speed: "ultrafast"));
        var claude = await SavePickerDefaultsAsync("claude", new("claude-fable-5-1[1m]", "high", "plan", true));
        var client = await ConnectAsync("codex-mcp-client");
        Assert.StartsWith("Created ", await CallAsync(client, "create_board_card", new()
        {
            ["title"] = "Assign later", ["assignee"] = ""
        }));
        var card = await CreatedCardAsync("Assign later");
        Assert.Null(card.BaseLlmOptions);

        Assert.StartsWith("Updated ", await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["assignee"] = "codex", ["managerSelection"] = "base:claude"
        }));
        var assigned = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Equal(codex, assigned.BaseLlmOptions);
        Assert.Equal(claude, assigned.ManagerBaseLlmOptions);

        var nextCodex = await SavePickerDefaultsAsync("codex", new("gpt-6-astra", "high"));
        await SavePickerDefaultsAsync("claude", new("claude-fable-5-1[1m]", "low"));
        await CallAsync(client, "update_board_card", new() { ["card"] = card.Key, ["priority"] = "high" });
        await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["assignee"] = "base:codex", ["managerSelection"] = "base:claude"
        });
        var preserved = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Equal(codex, preserved.BaseLlmOptions);
        Assert.Equal(claude, preserved.ManagerBaseLlmOptions);

        await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["assignee"] = "claude", ["managerSelection"] = "base:codex"
        });
        var changed = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Equal("low", changed.BaseLlmOptions!.Effort);
        Assert.Equal(nextCodex, changed.ManagerBaseLlmOptions);
    }

    [Fact]
    public async Task ExplicitOptionsAndClearFlagsWinOverDefaults_IncludingEmptyOptions()
    {
        await SavePickerDefaultsAsync("codex", new("gpt-6-astra", "xhigh", Yolo: true, Speed: "ultrafast"));
        await SavePickerDefaultsAsync("claude", new("claude-fable-5-1[1m]", "high", "plan", true));
        var client = await ConnectAsync("codex-mcp-client");
        await CallAsync(client, "create_board_card", new()
        {
            ["title"] = "Explicit options", ["assignee"] = "codex",
            ["baseLlmOptions"] = new BaseLlmOptions(Effort: "low"),
            ["managerSelection"] = "base:codex", ["managerBaseLlmOptions"] = new BaseLlmOptions()
        });
        var card = await CreatedCardAsync("Explicit options");
        Assert.Equal(BaseLlmOptionsBuilder.Normalize(LLM.Codex, new(Effort: "low")), card.BaseLlmOptions);
        Assert.Null(card.ManagerBaseLlmOptions);

        await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["assignee"] = "claude", ["baseLlmOptions"] = new BaseLlmOptions(),
            ["managerSelection"] = "base:claude", ["managerBaseLlmOptions"] = new BaseLlmOptions(Effort: "low")
        });
        var explicitChange = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Null(explicitChange.BaseLlmOptions);
        Assert.Equal(BaseLlmOptionsBuilder.Normalize(LLM.Claude, new(Effort: "low")), explicitChange.ManagerBaseLlmOptions);

        await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["assignee"] = "codex", ["clearBaseLlmOptions"] = true,
            ["managerSelection"] = "base:codex", ["clearManagerBaseLlmOptions"] = true
        });
        // Resending the same assignment must also preserve deliberately cleared settings.
        await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["assignee"] = "codex", ["managerSelection"] = "base:codex"
        });
        var cleared = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Null(cleared.BaseLlmOptions);
        Assert.Null(cleared.ManagerBaseLlmOptions);
    }

    [Theory]
    [InlineData("env:7:codex")]
    [InlineData("preset:preset_0123456789abcdef0123456789abcdef:codex")]
    public async Task SavedAgentsDoNotInheritBaseProviderDefaults(string selection)
    {
        await SavePickerDefaultsAsync("codex", new("gpt-6-astra", "xhigh", Yolo: true, Speed: "ultrafast"));
        var client = await ConnectAsync("codex-mcp-client");
        Assert.StartsWith("Created ", await CallAsync(client, "create_board_card", new()
        {
            ["title"] = "Saved agent", ["assignee"] = selection, ["managerSelection"] = selection
        }));
        var card = await CreatedCardAsync("Saved agent");
        Assert.Equal(selection, card.Assignee);
        Assert.Null(card.BaseLlmOptions);
        Assert.Null(card.ManagerBaseLlmOptions);
        Assert.StartsWith("FAIL:", await CallAsync(client, "update_board_card", new()
        {
            ["card"] = card.Key, ["baseLlmOptions"] = new BaseLlmOptions(Effort: "low")
        }));
    }

    [Fact]
    public async Task SharedServiceAppliesDefaultsToBothRoles_AndLeavesStoredDefaultsUntouched()
    {
        var saved = await SavePickerDefaultsAsync("codex", new("gpt-6-astra", "xhigh", Speed: "ultrafast"));
        var card = await _service.CreateCardAsync(_project, new(Title: "From the API", Assignee: "base:codex",
            WorkflowMode: "agent", ManagerSelection: "base:codex"), Ct);
        Assert.Equal(saved, card.BaseLlmOptions);
        Assert.Equal(saved, card.ManagerBaseLlmOptions);
        var json = await _store.GetLaunchDefaultsJsonAsync(LLM.Codex, Ct);
        await _service.UpdateCardAsync(_project, card.Id, new(BaseLlmOptions: new(), ManagerBaseLlmOptions: new()), Ct);
        Assert.Equal(json, await _store.GetLaunchDefaultsJsonAsync(LLM.Codex, Ct));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"speed\":\"invalid\"}")]
    [InlineData("null")]
    public async Task MalformedDefaultsFallBackWithoutRewritingPreferences(string json)
    {
        await SaveDefaultsCacheAsync(LlmLaunchDefaults.CacheKey(LLM.Codex), json, Ct);
        var client = await ConnectAsync("codex-mcp-client");
        Assert.StartsWith("Created ", await CallAsync(client, "create_board_card", new()
        {
            ["title"] = "Fallback", ["assignee"] = "codex", ["managerSelection"] = "base:codex"
        }));
        var card = await CreatedCardAsync("Fallback");
        Assert.Null(card.BaseLlmOptions);
        Assert.Null(card.ManagerBaseLlmOptions);
        Assert.Equal(json, await _store.GetLaunchDefaultsJsonAsync(LLM.Codex, Ct));
    }

    private async Task<BaseLlmOptions> SavePickerDefaultsAsync(string cli, BaseLlmOptions options)
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(r => r.SetGlobalCacheValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, string json, CancellationToken ct) => SaveDefaultsCacheAsync(key, json, ct));
        return await new LlmPickerPreferenceService(repository.Object).SaveLaunchDefaultsAsync(cli, options, Ct);
    }

    private async Task SaveDefaultsCacheAsync(string key, string json, CancellationToken ct)
    {
        await using var state = new SqliteConnection($"Data Source={Path.Combine(_root, "state.db")};Pooling=False");
        await state.OpenAsync(ct);
        await using var command = state.CreateCommand();
        command.CommandText = SqlStrings.CreateGlobalCacheTable + ";" + SqlStrings.UpsertGlobalCache;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", json);
        command.Parameters.AddWithValue("$updatedUTC", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
