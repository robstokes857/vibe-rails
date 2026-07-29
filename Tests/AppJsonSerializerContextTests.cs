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
                DataExportConfigured: true),
            AppJsonSerializerContext.Default.AppSettingsDto);

        Assert.Contains("""
            "dataExportConfigured":true
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
