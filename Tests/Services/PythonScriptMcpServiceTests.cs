using System.Text.Json;
using ModelContextProtocol.Protocol;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.PythonScripts;
using Xunit;

namespace Tests.Services;

public sealed class PythonScriptMcpServiceTests : IDisposable
{
    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"viberails-python-mcp-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveBuildsTypedSchemaAndCallMapsInputsToArgvWithoutAShell()
    {
        var scripts = new Mock<IPythonScriptService>(MockBehavior.Strict);
        scripts.Setup(service => service.AuthorizeMcpExposureAsync(
                "report.py", "2468", It.IsAny<CancellationToken>()))
            .ReturnsAsync("report.py");
        scripts.Setup(service => service.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScriptState("report.py"));

        IReadOnlyList<string>? capturedArguments = null;
        scripts.Setup(service => service.RunAsync(
                "report.py",
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, IReadOnlyList<string>?, CancellationToken>((_, arguments, _) =>
                capturedArguments = arguments)
            .ReturnsAsync(new PythonScriptRunResponse(
                "report.py", 0, false, "created report\n", "", 12, DateTime.UtcNow.ToString("O")));

        var store = new PythonScriptMcpConfigurationStore(_installDirectory);
        var service = new PythonScriptMcpService(scripts.Object, store);
        var saved = await service.SaveAsync(new PythonScriptMcpConfigurationRequest(
            "report.py",
            "python_create_report",
            "Create a report for the requested input file.",
            [
                new("input_path", "Input file to process.", "string", true, null, "positional", null),
                new("limit", "Maximum rows to include.", "integer", false, "5", "option", "--limit"),
                new("verbose", "Include verbose diagnostics.", "boolean", false, null, "option", "--verbose")
            ],
            "2468"), TestContext.Current.CancellationToken);

        Assert.Equal("python_create_report", Assert.Single(saved.Configurations).ToolName);
        var tool = Assert.Single(await service.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("python_create_report", tool.Name);
        Assert.Equal("python-script", tool.Meta!["viberailsCategory"]!.GetValue<string>());
        Assert.Equal("string", tool.InputSchema.GetProperty("properties")
            .GetProperty("input_path").GetProperty("type").GetString());
        Assert.Equal(5, tool.InputSchema.GetProperty("properties")
            .GetProperty("limit").GetProperty("default").GetInt64());
        Assert.Equal("input_path", tool.InputSchema.GetProperty("required")[0].GetString());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());

        var result = await service.CallAsync(
            "python_create_report",
            new Dictionary<string, JsonElement>
            {
                ["input_path"] = JsonSerializer.SerializeToElement("sales.csv"),
                ["verbose"] = JsonSerializer.SerializeToElement(true)
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal(
            ["sales.csv", "--limit", "5", "--verbose"],
            capturedArguments);
        Assert.Contains("created report", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        scripts.VerifyAll();
    }

    [Fact]
    public async Task EnablingRequiresTheSigningPinAndNeverPersistsIt()
    {
        var scripts = new Mock<IPythonScriptService>(MockBehavior.Strict);
        scripts.Setup(service => service.AuthorizeMcpExposureAsync(
                "safe.py", "secret-pin", It.IsAny<CancellationToken>()))
            .ReturnsAsync("safe.py");
        var store = new PythonScriptMcpConfigurationStore(_installDirectory);
        var service = new PythonScriptMcpService(scripts.Object, store);

        await service.SaveAsync(new PythonScriptMcpConfigurationRequest(
            "safe.py", "python_safe", "Run the approved safe task.", [], "secret-pin"),
            TestContext.Current.CancellationToken);

        var document = File.ReadAllText(Path.Combine(
            _installDirectory,
            PythonScriptMcpConfigurationStore.FileName));
        Assert.DoesNotContain("secret-pin", document, StringComparison.Ordinal);
        scripts.VerifyAll();
    }

    [Fact]
    public async Task ReservedNamesAndAmbiguousPositionalsAreRejected()
    {
        var scripts = new Mock<IPythonScriptService>(MockBehavior.Strict);
        scripts.Setup(service => service.AuthorizeMcpExposureAsync(
                "job.py", "1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync("job.py");
        var service = new PythonScriptMcpService(
            scripts.Object,
            new PythonScriptMcpConfigurationStore(_installDirectory));

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SaveAsync(
            new PythonScriptMcpConfigurationRequest(
                "job.py", "search_history", "Collision.", [], "1234"),
            TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SaveAsync(
            new PythonScriptMcpConfigurationRequest(
                "job.py", "python_job", "Run a job.",
                [
                    new("maybe", "Optional first value.", "string", false, null, "positional", null),
                    new("later", "Later required value.", "string", true, null, "positional", null)
                ],
                "1234"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AChangedOrUnsignedScriptFailsClosedAtInvocationTime()
    {
        var scripts = new Mock<IPythonScriptService>(MockBehavior.Strict);
        scripts.Setup(service => service.AuthorizeMcpExposureAsync(
                "job.py", "1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync("job.py");
        scripts.Setup(service => service.RunAsync(
                "job.py", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PythonScriptValidationException(
                "Script 'job.py' changed after it was approved. Sign it again before running."));
        var service = new PythonScriptMcpService(
            scripts.Object,
            new PythonScriptMcpConfigurationStore(_installDirectory));
        await service.SaveAsync(new PythonScriptMcpConfigurationRequest(
            "job.py", "python_job", "Run the approved job.", [], "1234"),
            TestContext.Current.CancellationToken);

        var result = await service.CallAsync(
            "python_job", null, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("Sign it again", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task StoreMovesConfigurationOnRenameAndRemovesItOnDelete()
    {
        var store = new PythonScriptMcpConfigurationStore(_installDirectory);
        await store.SaveAsync(new PythonScriptMcpConfiguration(
            "old.py", "python_old", "Run old.", []), TestContext.Current.CancellationToken);

        var renamed = await store.RenameAsync(
            "old.py", "new.py", TestContext.Current.CancellationToken);
        Assert.Equal("new.py", Assert.Single(renamed.Configurations).ScriptName);
        Assert.Equal("python_old", Assert.Single(renamed.Configurations).ToolName);

        var deleted = await store.DeleteAsync("new.py", TestContext.Current.CancellationToken);
        Assert.Empty(deleted.Configurations);
    }

    [Fact]
    public async Task ListingDynamicToolsDoesNotProbeForAPythonInterpreter()
    {
        var scriptsDirectory = Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory);
        Directory.CreateDirectory(scriptsDirectory);
        File.WriteAllText(Path.Combine(scriptsDirectory, "list-only.py"), "print('ok')\n");
        var discoveryCalls = 0;
        var store = new PythonScriptMcpConfigurationStore(_installDirectory);
        await store.SaveAsync(new PythonScriptMcpConfiguration(
            "list-only.py", "python_list_only", "List without launching Python.", []),
            TestContext.Current.CancellationToken);
        var scripts = new PythonScriptService(
            installDirectory: _installDirectory,
            mcpConfigurationStore: store,
            pythonRunnerProvider: () =>
            {
                discoveryCalls++;
                throw new InvalidOperationException("Discovery must be lazy.");
            });
        var service = new PythonScriptMcpService(scripts, store);

        var tools = await service.ListToolsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("python_list_only", Assert.Single(tools).Name);
        Assert.Equal(0, discoveryCalls);
    }

    private PythonScriptListResponse ScriptState(string name) => new(
        true,
        Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory),
        [new PythonScriptInfo(name, PythonScriptService.StatusApproved, null, null, 10, Path.Combine(_installDirectory, name))]);

    public void Dispose()
    {
        if (Directory.Exists(_installDirectory)) Directory.Delete(_installDirectory, recursive: true);
    }
}
