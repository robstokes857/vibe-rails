using System.Text.Json;
using VibeRails.DTOs;
using Xunit;

namespace Tests;

public sealed class AppJsonSerializerContextTests
{
    [Fact]
    public void IncludesUpdateComputerNameDto_ForMinimalApiBodyBinding()
    {
        var json = JsonSerializer.Serialize(
            new UpdateComputerNameDto("build-box"),
            AppJsonSerializerContext.Default.UpdateComputerNameDto);

        Assert.Equal("""{"computerName":"build-box"}""", json);
    }

    [Fact]
    public void TokenSavingsPostDto_KeepsItsWireShape()
    {
        // The publish endpoint keys its upsert on these exact names. Nothing in the suite talks
        // to the live server any more, so this is what pins the contract.
        var json = JsonSerializer.Serialize(
            new TokenSavingsPostDto("build-box", 2000),
            AppJsonSerializerContext.Default.TokenSavingsPostDto);

        Assert.Equal("""{"computerName":"build-box","totalTokensSaved":2000}""", json);
    }

    [Fact]
    public void TokenSaverPausePayload_KeepsTheNamesTheMeterReads()
    {
        // The browser runs its own countdown off pausedUntilUtc and suppresses the badge on
        // saverEnabled:false. Renaming either field silently stops the paused badge from ever
        // appearing — nothing throws, the meter just never hears about a pause.
        var json = JsonSerializer.Serialize(
            new TokenSaverPausePayload("2026-07-31T12:05:00.0000000Z", SaverEnabled: true),
            AppJsonSerializerContext.Default.TokenSaverPausePayload);

        Assert.Equal(
            """{"pausedUntilUtc":"2026-07-31T12:05:00.0000000Z","saverEnabled":true}""",
            json);
    }

    [Fact]
    public void TokenSaverPausePayload_Resume_SendsAnExplicitNullExpiry()
    {
        // A resume must be a value on the wire, not an omitted field: the meter clears its badge
        // when it sees a null expiry, and an absent key would leave the old countdown running.
        var json = JsonSerializer.Serialize(
            new TokenSaverPausePayload(null, SaverEnabled: true),
            AppJsonSerializerContext.Default.TokenSaverPausePayload);

        Assert.Equal("""{"pausedUntilUtc":null,"saverEnabled":true}""", json);
    }

    [Fact]
    public void TokenSaverPausePayload_OmittedSaverEnabled_ReadsAsOn()
    {
        // The MCP tool deserializes this from whatever proxy child its terminal happens to be
        // running, which can predate the field. Before it existed the endpoint only ever paused a
        // saver that was on, so absent must mean on: defaulting to false would make the tool report
        // "the saver is switched off" about a pause it had just successfully started.
        var payload = JsonSerializer.Deserialize(
            """{"pausedUntilUtc":"2026-07-31T12:05:00.0000000Z"}""",
            AppJsonSerializerContext.Default.TokenSaverPausePayload);

        Assert.NotNull(payload);
        Assert.True(payload!.SaverEnabled);
        Assert.Equal("2026-07-31T12:05:00.0000000Z", payload.PausedUntilUtc);
    }

    [Fact]
    public void AppSettingsDto_OmittedOptionalFields_BindToSafeDefaults()
    {
        // The stale-client guard: a cached app.js that predates these fields must not be read as
        // "clear the API key". Absent clearApiKey has to bind to null, never true.
        var dto = JsonSerializer.Deserialize(
            """
            {"remoteAccess":false,"apiKey":"","useVsCodeTheme":false,"mcpEnabled":true,
             "computerName":"","codexLlmProxyEnabled":false,"codexLlmProxyMode":"subscription",
             "claudeLlmProxyEnabled":false,"openCodeLlmProxyEnabled":false}
            """,
            AppJsonSerializerContext.Default.AppSettingsDto);

        Assert.NotNull(dto);
        Assert.Null(dto!.ClearApiKey);
        Assert.Null(dto.RemoveCoAuthorTrailers);
        Assert.Null(dto.RouteThroughVibeRailsAi);
        Assert.Null(dto.ShowVibeAiUi);
        Assert.Null(dto.DataExportOptIn);
        Assert.False(dto.DataExportConfigured);
        Assert.False(new VibeRails.Utils.Settings().DataExportOptIn);
    }

    [Fact]
    public void AppSettingsDto_HttpRelaySetting_UsesTheAppAotContext()
    {
        var dto = JsonSerializer.Deserialize(
            """{"remoteAccess":false,"apiKey":"","useVsCodeTheme":false,"mcpEnabled":true,"computerName":"","routeThroughVibeRailsAi":true}""",
            AppJsonSerializerContext.Default.AppSettingsDto);

        Assert.NotNull(dto);
        Assert.True(dto!.RouteThroughVibeRailsAi);
        Assert.False(new VibeRails.Utils.Settings().RouteThroughVibeRailsAi);
    }

    [Fact]
    public void AppSettingsDto_ClearApiKeyFlag_RoundTripsFromTheClient()
    {
        var dto = JsonSerializer.Deserialize(
            """
            {"remoteAccess":false,"apiKey":"","useVsCodeTheme":false,"mcpEnabled":true,
             "computerName":"","clearApiKey":true}
            """,
            AppJsonSerializerContext.Default.AppSettingsDto);

        Assert.NotNull(dto);
        Assert.True(dto!.ClearApiKey);
    }

    [Fact]
    public void AppSettingsDto_ReportsSessionSharingConfigurationAndConsent()
    {
        var json = JsonSerializer.Serialize(
            new AppSettingsDto(
                RemoteAccess: false,
                ApiKey: "••••1234",
                UseVsCodeTheme: false,
                McpEnabled: true,
                ComputerName: "build-box",
                CodexLlmProxyEnabled: false,
                CodexLlmProxyMode: "subscription",
                ClaudeLlmProxyEnabled: false,
                OpenCodeLlmProxyEnabled: false,
                ClaudeTokenSaverEnabled: true,
                CodexTokenSaverEnabled: true,
                OpenCodeTokenSaverEnabled: true,
                TokenSaverCaptureEnabled: false,
                MachineName: "build-box",
                ClearApiKey: null,
                DataExportConfigured: true,
                RemoveCoAuthorTrailers: true,
                DataExportOptIn: true),
            AppJsonSerializerContext.Default.AppSettingsDto);

        Assert.Contains("""
            "dataExportConfigured":true
            """, json);
        Assert.Contains("""
            "removeCoAuthorTrailers":true
            """, json);
        Assert.Contains("""
            "dataExportOptIn":true
            """, json);
    }

    [Fact]
    public void DataExportResponse_UsesCamelCaseAndSha256()
    {
        const string sha256 =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var json = JsonSerializer.Serialize(
            new DataExportResponse(
                Success: true,
                Status: "ok",
                Message: "Data exported successfully.",
                Sha256: sha256),
            AppJsonSerializerContext.Default.DataExportResponse);

        Assert.Equal(
            """{"success":true,"status":"ok","message":"Data exported successfully.","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""",
            json);
    }

    [Fact]
    public void StateDatabaseSizeResponse_UsesBytesOnTheWire()
    {
        var json = JsonSerializer.Serialize(
            new StateDatabaseSizeResponse(12_345_678),
            AppJsonSerializerContext.Default.StateDatabaseSizeResponse);

        Assert.Equal("""{"bytes":12345678}""", json);
    }

    [Fact]
    public void LlmPickerPreferences_UseTheAotContextAndStableWireNames()
    {
        var response = new LlmPickerPreferencesResponse([
            new LlmPickerPreferenceItem(
                "base:claude", "base", "Base CLIs", "Claude", "claude", null, true, 0)
        ]);

        var json = JsonSerializer.Serialize(
            response,
            AppJsonSerializerContext.Default.LlmPickerPreferencesResponse);
        var request = JsonSerializer.Deserialize(
            """{"items":[{"key":"base:claude","kind":"base","group":"Base CLIs","label":"Claude","cli":"claude","environmentId":null,"enabled":false,"order":0}]}""",
            AppJsonSerializerContext.Default.UpdateLlmPickerPreferencesRequest);

        Assert.Equal(
            """{"items":[{"key":"base:claude","kind":"base","group":"Base CLIs","label":"Claude","cli":"claude","environmentId":null,"enabled":true,"order":0}]}""",
            json);
        Assert.NotNull(request);
        Assert.False(Assert.Single(request!.Items!).Enabled);
    }

    [Fact]
    public void FileSystemBrowseResponse_UsesTheAotContextAndStableWireNames()
    {
        var response = new FileSystemBrowseResponse(
            DefaultPath: "/repo",
            CurrentPath: "/repo/src",
            CurrentName: "src",
            ParentPath: "/repo",
            Breadcrumbs: [new FileSystemLocationResponse("src", "/repo/src")],
            Roots: [new FileSystemLocationResponse("/", "/")],
            Entries:
            [
                new FileSystemEntryResponse(
                    "index.js",
                    "/repo/src/index.js",
                    "file",
                    IsHidden: false,
                    IsSymbolicLink: false,
                    Size: 42,
                    LastModifiedUtc: DateTimeOffset.UnixEpoch,
                    Extension: ".js")
            ],
            Truncated: true,
            NextCursor: "cursor-value",
            TotalCount: 42,
            Search: "index");

        var json = JsonSerializer.Serialize(
            response,
            AppJsonSerializerContext.Default.FileSystemBrowseResponse);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entry = root.GetProperty("entries")[0];

        Assert.Equal("/repo/src", root.GetProperty("currentPath").GetString());
        Assert.Equal("/repo", root.GetProperty("parentPath").GetString());
        Assert.Equal("index.js", entry.GetProperty("name").GetString());
        Assert.Equal("file", entry.GetProperty("kind").GetString());
        Assert.False(entry.GetProperty("isSymbolicLink").GetBoolean());
        Assert.Equal(42, entry.GetProperty("size").GetInt64());
        Assert.Equal("cursor-value", root.GetProperty("nextCursor").GetString());
        Assert.Equal(42, root.GetProperty("totalCount").GetInt64());
        Assert.Equal("index", root.GetProperty("search").GetString());
        Assert.True(root.TryGetProperty("breadcrumbs", out _));
        Assert.True(root.TryGetProperty("roots", out _));
    }

    [Fact]
    public void TerminalSnapshotResponse_UsesReservedXtermRendererFieldNames()
    {
        var json = JsonSerializer.Serialize(
            new TerminalSnapshotResponse(
                TabId: "tab-1",
                SessionId: "session-1",
                CapturedUtc: DateTimeOffset.UnixEpoch,
                Cols: 120,
                Rows: 30,
                ScreenText: ["prompt>"],
                XtermUiBytes: new TerminalXtermUiBytes(
                    ContentType: "application/vnd.viberails.xterm-ui-bytes",
                    Encoding: "base64",
                    Format: "ansi-replay",
                    Base64: "cHJvbXB0Pg==",
                    ByteLength: 7,
                    Cols: 120,
                    Rows: 30,
                    IncludesScrollback: true,
                    RendererHint: "xterm.js"),
                XtermPngString: null),
            AppJsonSerializerContext.Default.TerminalSnapshotResponse);

        Assert.Contains("\"xterm_ui_bytes\"", json);
        Assert.Contains("\"xterm_png_string\"", json);
        Assert.Contains("\"byte_length\"", json);
        Assert.Contains("\"includes_scrollback\"", json);
        Assert.DoesNotContain("\"xtermUiBytes\"", json);
    }

    [Fact]
    public void EnvironmentStepDto_KeepsTheNamesTheStepsEditorReads()
    {
        var json = JsonSerializer.Serialize(
            new EnvironmentStepDto("5f0f70a5-2f6d-4c8e-9a3b-0d1e2f3a4b5c", 1, 0, "Push", "git push", true, 120, false),
            AppJsonSerializerContext.Default.EnvironmentStepDto);

        Assert.Equal(
            """{"id":"5f0f70a5-2f6d-4c8e-9a3b-0d1e2f3a4b5c","phase":1,"position":0,"name":"Push","command":"git push","startMinimized":true,"timeoutSeconds":120,"enabled":false}""",
            json);
    }

    [Fact]
    public void EnvironmentStepRequest_OmittedOptionalFields_BindToTheDocumentedDefaults()
    {
        // A step the client sends with only a phase and a command must arrive enabled, windowed,
        // and on the 10-minute default rather than as a disabled zero-timeout row.
        var request = JsonSerializer.Deserialize(
            """{"phase":0,"name":"","command":"npm ci"}""",
            AppJsonSerializerContext.Default.EnvironmentStepRequest);

        Assert.NotNull(request);
        Assert.True(request!.Enabled);
        Assert.False(request.StartMinimized);
        Assert.Equal(EnvironmentStep.DefaultTimeoutSeconds, request.TimeoutSeconds);
    }

    [Fact]
    public void UpdateEnvironmentRequest_OmittedSteps_BindToNullNotAnEmptyList()
    {
        // null is "leave them untouched" and [] is "clear them". Binding an absent key to an empty
        // list would make every environment save wipe its steps.
        var request = JsonSerializer.Deserialize(
            """{"name":"review","customArgs":"--yolo"}""",
            AppJsonSerializerContext.Default.UpdateEnvironmentRequest);

        Assert.NotNull(request);
        Assert.Null(request!.Steps);
    }

    [Fact]
    public void EnvironmentStepTestEvent_UsesTheWireNamesTheConsoleReads()
    {
        var line = JsonSerializer.Serialize(
            new EnvironmentStepTestEvent("line", "installing deps", IsError: true),
            AppJsonSerializerContext.Default.EnvironmentStepTestEvent);
        var done = JsonSerializer.Serialize(
            new EnvironmentStepTestEvent("done", ExitCode: 3, DurationMs: 1200, Message: "exited with code 3"),
            AppJsonSerializerContext.Default.EnvironmentStepTestEvent);

        Assert.Contains("""{"type":"line","line":"installing deps","isError":true""", line);
        Assert.Contains("""{"type":"done","line":null,"isError":false,"exitCode":3,"durationMs":1200""", done);
    }

    [Fact]
    public void EnvironmentStepFailedPayload_CarriesWhatTheToastNeeds()
    {
        var json = JsonSerializer.Serialize(
            new EnvironmentStepFailedPayload("s-1", "review", 0, "Install", 1, false, "Step \"Install\" exited with code 1."),
            AppJsonSerializerContext.Default.EnvironmentStepFailedPayload);

        Assert.Contains("\"sessionId\":\"s-1\"", json);
        Assert.Contains("\"environmentName\":\"review\"", json);
        Assert.Contains("\"stepName\":\"Install\"", json);
        Assert.Contains("\"exitCode\":1", json);
        Assert.Contains("\"timedOut\":false", json);
    }
}
