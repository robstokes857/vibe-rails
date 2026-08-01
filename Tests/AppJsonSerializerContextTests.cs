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
        Assert.False(dto.DataExportConfigured);
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
    public void AppSettingsDto_ReportsDataExportConfigured_SoTheClientCanGateTheButton()
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
                RemoveCoAuthorTrailers: true),
            AppJsonSerializerContext.Default.AppSettingsDto);

        Assert.Contains("""
            "dataExportConfigured":true
            """, json);
        Assert.Contains("""
            "removeCoAuthorTrailers":true
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

}
