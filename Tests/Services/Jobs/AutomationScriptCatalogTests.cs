using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class AutomationScriptCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vb-script-catalog-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public AutomationScriptCatalogTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task DiscoveryComparesCurrentBytesAndRejectsUnsafeOrUnavailableScriptsWithoutSaving()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "check.py"), "print('ok')", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "check.sh"), "echo ok", Ct);
        var resolver = new Mock<IJobExecutableResolver>();
        resolver.Setup(r => r.Resolve(JobScriptRuntime.Python)).Returns(new JobExecutable("python", []));
        var scripts = new AutomationScriptService(resolver.Object);
        var approved = await scripts.NormalizeAsync(root, new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python), Ct);
        var action = new JobActionRecord("a", 1, 0, JobActionKind.Script, null, null, LLM.NotSet, "check.py", JobScriptRuntime.Python, [], null, null, approved.ApprovedHash);
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(j => j.GetJobsAsync(root, false, It.IsAny<CancellationToken>())).ReturnsAsync([
            new JobDefinitionRecord(1, "Check", root, LLM.NotSet, null, null, "", null, true, DateTime.UtcNow, DateTime.UtcNow, null, [], Actions: [action])]);
        var files = new Mock<IBoardFileIndexService>();
        files.Setup(f => f.GetScriptPathsAsync(root, It.IsAny<CancellationToken>())).ReturnsAsync(new BoardFileSearchResponse(["check.py", "check.sh", "../outside.py"], false));
        var service = new AutomationScriptCatalogService(files.Object, scripts, jobs.Object);
        var catalog = await service.ReadAsync(root, Ct);
        Assert.True(catalog.Scripts[0].Approved);
        Assert.Null(catalog.Scripts[0].UnavailableReason);
        Assert.NotNull(catalog.Scripts[1].UnavailableReason);
        Assert.NotNull(catalog.Scripts[2].UnavailableReason);
        await File.WriteAllTextAsync(Path.Combine(root, "check.py"), "print('changed')", Ct);
        Assert.False((await service.ReadAsync(root, Ct)).Scripts[0].Approved);
        Assert.Equal(approved.ApprovedHash, action.ApprovedHash);
        jobs.Verify(j => j.GetJobsAsync(root, false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        jobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileIndexFindsScriptsBeyondTheOrdinarySearchPage()
    {
        var names = Enumerable.Range(0, 70).Select(i => $"a{i:000}.txt").Concat(["scripts/z.py", "scripts/z.PS1", "scripts/z.sh", "not.py.txt"]).ToList();
        var files = new BoardFileIndexService(TimeProvider.System, (_, _) => Task.FromResult<IReadOnlyList<string>?>(names));
        var result = await files.GetScriptPathsAsync(root, Ct);
        Assert.Equal(3, result.Files.Count);
        Assert.False(result.Truncated);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
