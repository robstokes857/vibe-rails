using Moq;
using PyBridge;
using VibeRails.DTOs;
using VibeRails.Services.PythonScripts;
using Xunit;

namespace Tests.Services;

public sealed class UserScriptLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vb-user-scripts", Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "install");
    private string RepoA => Path.Combine(_root, "repo-a");
    private string RepoB => Path.Combine(_root, "repo-b");
    private PythonScriptService Service(string? project = null) => new(installDirectory: Install,
        projectRootProvider: () => project ?? RepoA);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string FileIn(string directory, string name = "launch.py")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "print('hello')\n");
        return path;
    }

    [Fact]
    public async Task NoFolderIsAutomaticallyDiscoveredAndInternalScriptsCannotBeRegistered()
    {
        var internalPath = FileIn(Path.Combine(Install, "scripts"));
        FileIn(Path.Combine(Install, PythonScriptService.ScriptsSubdirectory), "unused.py");
        var service = Service();
        Assert.Empty((await service.GetStatusAsync(Ct)).Scripts);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ImportAsync(new(internalPath), Ct));
        Assert.True(File.Exists(internalPath));
        Assert.Empty((await service.GetStatusAsync(Ct)).Scripts);
    }

    [Fact]
    public async Task SameNamedFilesHaveIndependentIdsScopesAndSignatures()
    {
        var a = Service(RepoA);
        var b = Service(RepoB);
        var pathA = FileIn(RepoA);
        var pathB = FileIn(RepoB);
        var entryA = Assert.Single((await a.ImportAsync(new(pathA, DisplayName: "Start A", Scope: "repo"), Ct)).Scripts);
        var entryB = Assert.Single((await b.ImportAsync(new(pathB, DisplayName: "Start B", Scope: "repo"), Ct)).Scripts);
        Assert.NotEqual(entryA.Id, entryB.Id);
        Assert.Equal(pathA, entryA.Path);
        Assert.Equal("Start A", entryA.DisplayName);
        await a.SetPinAsync(new(null, "1234"), Ct);
        await a.ApproveAsync(new(entryA.Id, "1234"), Ct);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => b.GetContentAsync(entryA.Id, Ct));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => b.ApproveAsync(new(entryA.Id, "1234"), Ct));
        Assert.Equal("unapproved", Assert.Single((await b.GetStatusAsync(Ct)).Scripts).Status);
        await a.UpdateSettingsAsync(new(entryA.Id!, "Start everywhere", "global", false), Ct);
        var global = (await b.GetStatusAsync(Ct)).Scripts.Single(item => item.Id == entryA.Id);
        Assert.Equal("Start everywhere", global.DisplayName);
        Assert.Equal("approved", global.Status);
        Assert.False(File.Exists(Path.Combine(Install, PythonScriptService.ScriptsSubdirectory, "launch.py")));
    }

    [Fact]
    public async Task ManagedAndChosenLocationsCreateFilesWithoutOverwritingExistingOnes()
    {
        var service = Service();
        var first = Assert.Single((await service.CreateAsync(new("managed.py", "print(1)", Scope: "repo"), Ct)).Scripts);
        Assert.Equal(Path.Combine(Install, "scripts", "UserScripts", "managed.py"), first.Path);
        Assert.Equal("managed.py", first.DisplayName);
        var list = await service.CreateAsync(new("local.py", "print(2)", Directory: RepoA, Scope: "repo"), Ct);
        Assert.Contains(list.Scripts, item => item.Path == Path.Combine(RepoA, "local.py"));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.CreateAsync(new("local.py", "overwrite", Directory: RepoA), Ct));
        Assert.Equal("print(2)", File.ReadAllText(Path.Combine(RepoA, "local.py")));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ImportAsync(new(FileIn(RepoB), Scope: "repo"), Ct));
    }

    [Fact]
    public async Task NewRunPinRequirementSurvivesInstancesAndCannotBeDisabledWithoutThePin()
    {
        var service = Service();
        var entry = Assert.Single((await service.ImportAsync(new(FileIn(RepoA), RequirePinEachRun: true), Ct)).Scripts);
        await service.SetPinAsync(new(null, "1234"), Ct);
        await service.ApproveAsync(new(entry.Id, "1234"), Ct);
        var reopened = Service();
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => reopened.ValidateRunAuthorizationAsync(entry.Id, null, Ct));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => reopened.ValidateRunAuthorizationAsync(entry.Id, "wrong", Ct));
        await reopened.ValidateRunAuthorizationAsync(entry.Id, "1234", Ct);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => reopened.UpdateSettingsAsync(new(entry.Id!, "Launch", "global", false), Ct));
        await reopened.UpdateSettingsAsync(new(entry.Id!, "Launch", "global", false, "1234"), Ct);
        await reopened.ValidateRunAuthorizationAsync(entry.Id, null, Ct);
    }

    [Fact]
    public async Task RunUsesVerifiedCacheAndKeepsTheOriginalWorkingDirectory()
    {
        var path = FileIn(RepoA);
        var runner = new Mock<IPythonRunner>();
        runner.SetupGet(item => item.Options).Returns(new PythonRunnerOptions());
        PythonRunnerOptions? options = null;
        string? executedPath = null;
        runner.Setup(item => item.RunAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(),
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(call => {
                executedPath = ((IReadOnlyList<string>)call.Arguments[0])[0];
                File.WriteAllText(path, "print('changed after verification')\n");
                Assert.Equal("print('hello')\n", File.ReadAllText(executedPath));
            }))
            .ReturnsAsync(new PythonResult { ExitCode = 0, StandardOutput = "hello", StandardError = "", Executable = "python", CommandLine = "python", RunTime = TimeSpan.Zero });
        var service = new PythonScriptService(runner.Object, Install,
            runnerFactory: value => { options = value; return runner.Object; }, projectRootProvider: () => RepoA);
        var entry = Assert.Single((await service.ImportAsync(new(path), Ct)).Scripts);
        await service.SetPinAsync(new(null, "1234"), Ct);
        await service.ApproveAsync(new(entry.Id, "1234"), Ct);
        await service.RunAsync(entry.Id, Ct);
        Assert.Equal(RepoA, options!.WorkingDirectory);
        Assert.Equal(Path.Combine(RepoA, ".vb-scripts"), Path.GetDirectoryName(executedPath));
        Assert.NotEqual(path, executedPath);
        Assert.True(File.Exists(executedPath));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RunAsync(entry.Id, Ct));
    }

    [Fact]
    public async Task RemovingAndAddingAgainDoesNotRecoverApprovalOrDeleteTheFile()
    {
        var service = Service();
        var path = FileIn(RepoA);
        var entry = Assert.Single((await service.ImportAsync(new(path), Ct)).Scripts);
        await service.SetPinAsync(new(null, "1234"), Ct);
        await service.ApproveAsync(new(entry.Id, "1234"), Ct);
        await service.DeleteAsync(entry.Id, Ct);
        Assert.True(File.Exists(path));
        var added = Assert.Single((await service.ImportAsync(new(path), Ct)).Scripts);
        Assert.NotEqual(entry.Id, added.Id);
        Assert.Equal("unapproved", added.Status);
    }

    [Fact]
    public async Task WindowsAliasCannotExposeInternalScripts()
    {
        if (!OperatingSystem.IsWindows()) return;
        FileIn(Path.Combine(Install, "scripts"));
        var alias = Path.Combine(Install, "scripts.", "launch.py");
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => Service().ImportAsync(new(alias), Ct));
    }

    [Fact]
    public async Task ConcurrentRegistrationRetainsBothEntries()
    {
        var a = Service();
        var b = Service();
        var first = FileIn(RepoA);
        var second = FileIn(RepoB);
        await Task.WhenAll(a.ImportAsync(new(first), Ct), b.ImportAsync(new(second), Ct));
        Assert.Equal(2, (await Service().GetStatusAsync(Ct)).Scripts.Count);
    }
}
