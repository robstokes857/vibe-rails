using Moq;
using PyBridge;
using VibeRails.DTOs;
using VibeRails.Services.Git;
using VibeRails.Services.PythonScripts;
using Xunit;

namespace Tests.Services;

public sealed class PythonScriptServiceTests : IDisposable
{
    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(), "vb-pyscript-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_installDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task PinLifecycle_FirstSetChangeAndWrongCurrentPin()
    {
        var service = NewService(out _);

        var afterSet = await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        Assert.True(afterSet.PinConfigured);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SetPinAsync(
            new SetPythonScriptPinRequest("9999", "5678"), TestContext.Current.CancellationToken));

        var afterChange = await service.SetPinAsync(
            new SetPythonScriptPinRequest("1234", "5678"), TestContext.Current.CancellationToken);
        Assert.True(afterChange.PinConfigured);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "12"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApproveTracksTheExactContent_EditFlipsToModified_ReapproveHeals()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "wrong"), TestContext.Current.CancellationToken));

        var approved = await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            approved.Scripts.Single(script => script.Name == "job.py").Status);

        await WriteScriptAsync("job.py", "print('two')\n");
        var modified = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusModified,
            modified.Scripts.Single(script => script.Name == "job.py").Status);

        var healed = await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            healed.Scripts.Single(script => script.Name == "job.py").Status);

        var revoked = await service.RevokeAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusUnapproved,
            revoked.Scripts.Single(script => script.Name == "job.py").Status);
    }

    [Fact]
    public async Task LineEndingAndBomChurnDoesNotInvalidateAnApproval()
    {
        var service = NewService(out _);
        await WriteScriptAsync("stable.py", "a = 1\nb = 2\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("stable.py", "1234"), TestContext.Current.CancellationToken);

        // CRLF rewrite + BOM: what git autocrlf or a Windows editor does to the same code.
        File.WriteAllBytes(
            ScriptPath("stable.py"),
            [0xEF, 0xBB, 0xBF, .. "a = 1\r\nb = 2\r\n"u8]);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            status.Scripts.Single(script => script.Name == "stable.py").Status);
    }

    [Fact]
    public async Task RunRefusesUnsignedAndModifiedScriptsWithoutExecutingAnything()
    {
        var service = NewService(out var runner);
        await WriteScriptAsync("danger.py", "print('x')\n");

        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("danger.py", TestContext.Current.CancellationToken));

        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("danger.py", "1234"), TestContext.Current.CancellationToken);
        await WriteScriptAsync("danger.py", "print('tampered')\n");

        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("danger.py", TestContext.Current.CancellationToken));

        runner.Verify(
            item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunExecutesAVerifiedCachedCopyAndRecordsHistory()
    {
        var service = NewService(out var runner);
        await WriteScriptAsync("good.py", "print('ok')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("good.py", "1234"), TestContext.Current.CancellationToken);

        IReadOnlyList<string>? executedArguments = null;
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
                executedArguments = (IReadOnlyList<string>)invocation.Arguments[0]))
            .ReturnsAsync(new PythonResult
            {
                ExitCode = 0,
                StandardOutput = "ok",
                StandardError = "",
                RunTime = TimeSpan.FromMilliseconds(12),
                Executable = "python",
                CommandLine = "python good.py"
            });

        var result = await service.RunAsync("good.py", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.StandardOutput);
        Assert.NotNull(executedArguments);
        var executedPath = Assert.Single(executedArguments);
        // The exact verified bytes run from the cache, never the still-writable original.
        Assert.NotEqual(ScriptPath("good.py"), executedPath);
        Assert.Equal(ScriptPath(".vb-scripts"), Path.GetDirectoryName(executedPath));
        Assert.True(File.Exists(executedPath), "The verified copy remains available for reuse.");
        Assert.Matches(@"^\.vibe-rails-[0-9a-f]{64}\.py$", Path.GetFileName(executedPath));
        Assert.Equal(Assert.Single((await service.GetStatusAsync(TestContext.Current.CancellationToken)).Scripts).Id, Assert.Single(service.GetRunHistory().Runs).Name);
    }

    [Fact]
    public async Task ConcurrentInstancesReuseTheSignedCacheWithoutRewritingIt()
    {
        var (service, runner) = await SignedService("good.py", "print('approved')\n");
        ReturnExecutedContent(runner);
        var reopened = new PythonScriptService(runner.Object, _installDirectory, runnerFactory: _ => runner.Object);
        await Task.WhenAll(service.RunAsync("good.py", TestContext.Current.CancellationToken),
            reopened.RunAsync("good.py", TestContext.Current.CancellationToken));
        var path = Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
        var writtenUtc = File.GetLastWriteTimeUtc(path);
        var runs = await Task.WhenAll(
            service.RunAsync("good.py", TestContext.Current.CancellationToken),
            reopened.RunAsync("good.py", TestContext.Current.CancellationToken));

        Assert.All(runs, run => Assert.Equal("print('approved')\n", run.StandardOutput));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py")));
        Assert.Equal(writtenUtc, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task AChangedCacheIsRepairedFromVerifiedBytesBeforeExecution()
    {
        var (service, runner) = await SignedService("good.py", "print('approved')\n");
        ReturnExecutedContent(runner);
        await service.RunAsync("good.py", TestContext.Current.CancellationToken);
        var path = Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py"));
        await File.WriteAllTextAsync(path, "print('injected')\n", TestContext.Current.CancellationToken);

        var run = await service.RunAsync("good.py", TestContext.Current.CancellationToken);

        Assert.Equal("print('approved')\n", run.StandardOutput);
        Assert.Equal(path, Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5 * 1024 * 1024)]
    [InlineData(5 * 1024 * 1024 + 1)]
    public async Task ConcurrentInstancesRepairADifferentLengthCacheBeforeExecution(int cacheLength)
    {
        var ct = TestContext.Current.CancellationToken;
        const string content = "print('approved')\n";
        var (service, runner) = await SignedService("good.py", content);
        ReturnExecutedContent(runner);
        await service.RunAsync("good.py", ct);
        var path = Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py"));
        using (var cache = File.OpenWrite(path)) cache.SetLength(cacheLength);
        Assert.Equal(PythonScriptService.StatusApproved,
            Assert.Single((await service.GetStatusAsync(ct)).Scripts).Status);

        var reopened = new PythonScriptService(runner.Object, _installDirectory, runnerFactory: _ => runner.Object);
        var runs = await Task.WhenAll(service.RunAsync("good.py", ct), reopened.RunAsync("good.py", ct));

        Assert.All(runs, run => Assert.Equal(content, run.StandardOutput));
        Assert.Equal(content, (await service.RunAsync("good.py", ct)).StandardOutput);
        Assert.Equal(content, await File.ReadAllTextAsync(path, ct));
        Assert.Equal(content, await File.ReadAllTextAsync(ScriptPath("good.py"), ct));
        Assert.Empty(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOversizedOriginalIsStillRejectedWithAValidCache(bool interactive)
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, runner) = await SignedService("good.py", "print('approved')\n");
        ReturnExecutedContent(runner);
        await service.RunAsync("good.py", ct);
        var path = Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py"));
        using (var original = File.OpenWrite(ScriptPath("good.py"))) original.SetLength(5 * 1024 * 1024 + 1);

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(() => interactive
            ? (Task)service.RunInteractiveAsync("good.py", ct)
            : service.RunAsync("good.py", ct));
        Assert.Contains("5 MB limit", error.Message);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() =>
            service.ApproveAsync(new PythonScriptApprovalRequest("good.py", "1234"), ct));
        Assert.Single(service.GetRunHistory().Runs);
        Assert.Equal("print('approved')\n", await File.ReadAllTextAsync(path, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOversizedLinkedCacheIsRejectedWithoutReplacingItOrItsTarget(bool interactive)
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, runner) = await SignedService("good.py", "print('approved')\n");
        ReturnExecutedContent(runner);
        await service.RunAsync("good.py", ct);
        var path = Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py"));
        var outside = Path.Combine(_installDirectory, "outside.py");
        using (var target = File.Create(outside)) target.SetLength(5 * 1024 * 1024 + 1);
        File.Delete(path);
        try
        {
            File.CreateSymbolicLink(path, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This platform does not permit creating file symbolic links.");
        }

        try
        {
            var error = await Assert.ThrowsAsync<PythonScriptValidationException>(() => interactive
                ? (Task)service.RunInteractiveAsync("good.py", ct)
                : service.RunAsync("good.py", ct));
            Assert.Contains("reparse", error.Message);
            Assert.NotNull(new FileInfo(path).LinkTarget);
            Assert.Equal(5 * 1024 * 1024 + 1, new FileInfo(outside).Length);
            Assert.Single(service.GetRunHistory().Runs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ANewSignedVersionGetsANewCacheAndCachedFilesNeverBypassApproval()
    {
        var (service, runner) = await SignedService("good.py", "print('first')\n");
        ReturnExecutedContent(runner);
        await service.RunAsync("good.py", TestContext.Current.CancellationToken);
        var firstPath = Assert.Single(Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py"));
        await WriteScriptAsync("good.py", "print('second')\n");
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RunAsync("good.py", TestContext.Current.CancellationToken));
        await service.ApproveAsync(new PythonScriptApprovalRequest("good.py", "1234"), TestContext.Current.CancellationToken);
        var second = await service.RunAsync("good.py", TestContext.Current.CancellationToken);

        Assert.Equal("print('second')\n", second.StandardOutput);
        Assert.Equal(2, Directory.GetFiles(ScriptPath(".vb-scripts"), "*.py").Length);
        Assert.Equal("print('first')\n", await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken));
        await service.RevokeAsync(new PythonScriptApprovalRequest("good.py", "1234"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RunAsync("good.py", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunKeepsNewAndExistingCachesOutOfGit(bool existingCache)
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, runner) = await SignedService("good.py", "print('approved')\n");
        ReturnExecutedContent(runner);
        var root = service.GetScriptsDirectory();
        var initialized = await GitCli.RunAsync(root, ["init"], ct);
        Assert.True(initialized.Succeeded, initialized.StdErr);
        const string repositoryRules = "# User rules\n*.log\n";
        if (existingCache)
        {
            Directory.CreateDirectory(ScriptPath(".vb-scripts"));
            await File.WriteAllTextAsync(ScriptPath(".vb-scripts/older-version.py"), "print('older')\n", ct);
            await File.WriteAllTextAsync(ScriptPath(".gitignore"), repositoryRules, ct);
        }

        await service.RunAsync("good.py", ct);
        var status = await GitCli.RunAsync(root,
            ["-c", "core.excludesFile=", "status", "--porcelain", "--untracked-files=all", "--", ".vb-scripts"], ct);
        Assert.True(status.Succeeded, status.StdErr);
        Assert.Empty(status.StdOut);
        var added = await GitCli.RunAsync(root, ["-c", "core.excludesFile=", "add", "--all"], ct);
        Assert.True(added.Succeeded, added.StdErr);
        var tracked = await GitCli.RunAsync(root, ["ls-files"], ct);
        Assert.True(tracked.Succeeded, tracked.StdErr);
        Assert.Equal(existingCache ? ".gitignore\ngood.py\n" : "good.py\n", tracked.StdOut.Replace("\r\n", "\n"));
        if (existingCache)
            Assert.Equal(repositoryRules, await File.ReadAllTextAsync(ScriptPath(".gitignore"), ct));
        else
            Assert.False(File.Exists(ScriptPath(".gitignore")));

        // An existing ignore file is retained when a later run reuses the cache.
        var ignorePath = ScriptPath(".vb-scripts/.gitignore");
        const string cacheRules = "# Local cache rules\n*\n";
        await File.WriteAllTextAsync(ignorePath, cacheRules, ct);
        await service.RunAsync("good.py", ct);
        Assert.Equal(cacheRules, await File.ReadAllTextAsync(ignorePath, ct));
    }

    private static void ReturnExecutedContent(Mock<IPythonRunner> runner) =>
        runner.Setup(item => item.RunAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<string> arguments, string? input, Action<string>? output,
                Action<string>? error, CancellationToken token) => Task.FromResult(new PythonResult
            {
                ExitCode = 0, StandardOutput = File.ReadAllText(arguments[0]), StandardError = "",
                RunTime = TimeSpan.Zero, Executable = "python", CommandLine = "python"
            }));

    [Theory]
    [InlineData("../evil.py")]
    [InlineData("..\\evil.py")]
    [InlineData("sub/evil.py")]
    [InlineData(".hidden.py")]
    [InlineData("notpython.txt")]
    public async Task RejectsPathTraversalAndNonScriptNames(string name)
    {
        var service = NewService(out _);
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ApproveAsync(
            new PythonScriptApprovalRequest(name, "1234"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptSigningFileIsTreatedAsEmptyState()
    {
        Directory.CreateDirectory(_installDirectory);
        File.WriteAllText(
            Path.Combine(_installDirectory, PythonScriptService.SigningFileName), "{broken");
        var service = NewService(out _);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.False(status.PinConfigured);
        Assert.Empty(status.Scripts);
    }

    [Fact]
    public async Task RunExecutesARealScriptEndToEndWhenPythonIsAvailable()
    {
        var options = PythonRunnerOptions.Discover();
        var probe = new PythonRunner(options);
        try
        {
            var version = await probe.RunAsync(
                ["--version"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.SkipWhen(version.ExitCode != 0, "No usable Python interpreter on this machine.");
        }
        catch (Exception)
        {
            Assert.Skip("No usable Python interpreter on this machine.");
        }

        var service = new PythonScriptService(probe, _installDirectory);
        await WriteScriptAsync("hello.py", "print('hello from viberails')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("hello.py", "1234"), TestContext.Current.CancellationToken);

        var result = await service.RunAsync("hello.py", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello from viberails", result.StandardOutput);
    }

    [Fact]
    public void CanonicalHashRejectsInvalidUtf8SoLatin1BytesCannotCollide()
    {
        // Lenient decoding mapped BOTH 0xE8 (e-grave) and 0xE9 (e-acute) to U+FFFD, so two
        // different latin-1 scripts hashed identically and one signature covered both.
        byte[] eGrave = [.. "x = '"u8, 0xE8, .. "'\n"u8];
        byte[] eAcute = [.. "x = '"u8, 0xE9, .. "'\n"u8];

        var first = Assert.Throws<PythonScriptValidationException>(
            () => PythonScriptService.ComputeCanonicalHash("a.py", eGrave));
        Assert.Contains("UTF-8", first.Message);
        Assert.Throws<PythonScriptValidationException>(
            () => PythonScriptService.ComputeCanonicalHash("a.py", eAcute));

        // The same two characters properly UTF-8 encoded are accepted and stay distinct.
        Assert.NotEqual(
            PythonScriptService.ComputeCanonicalHash("a.py", "x = 'è'\n"u8.ToArray()),
            PythonScriptService.ComputeCanonicalHash("a.py", "x = 'é'\n"u8.ToArray()));

        // A case-only rename is still a rename: the name is mixed in case-sensitively.
        Assert.NotEqual(
            PythonScriptService.ComputeCanonicalHash("Job.py", "print(1)\n"u8.ToArray()),
            PythonScriptService.ComputeCanonicalHash("job.py", "print(1)\n"u8.ToArray()));
    }

    [Fact]
    public async Task InvalidUtf8ScriptCannotBeSignedAndFlipsAnApprovedScriptToModified()
    {
        var service = NewService(out var runner);
        await WriteScriptAsync("legacy.py", "x = 'a'\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("legacy.py", "1234"), TestContext.Current.CancellationToken);

        // Rewritten as latin-1 (0xE9 = e-acute): not valid UTF-8.
        File.WriteAllBytes(ScriptPath("legacy.py"), [.. "x = '"u8, 0xE9, .. "'\n"u8]);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusModified,
            status.Scripts.Single(script => script.Name == "legacy.py").Status);

        var refused = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ApproveAsync(
            new PythonScriptApprovalRequest("legacy.py", "1234"), TestContext.Current.CancellationToken));
        Assert.Contains("UTF-8", refused.Message);
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("legacy.py", TestContext.Current.CancellationToken));
        runner.Verify(
            item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApprovalsKeyOffTheOnDiskFileNameCasing()
    {
        var service = NewService(out var runner);
        await WriteScriptAsync("Nightly.py", "print(1)\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);

        var approved = await service.ApproveAsync(
            new PythonScriptApprovalRequest("Nightly.py", "1234"), TestContext.Current.CancellationToken);
        var entry = Assert.Single(approved.Scripts);
        Assert.Equal("Nightly.py", entry.Name);
        Assert.Equal(PythonScriptService.StatusApproved, entry.Status);

        // On a case-insensitive volume a differently-cased request folds back to the one
        // on-disk entry instead of creating a second, lowercased approval.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Case-folding behaviour is volume-specific.");
        var reapproved = await service.ApproveAsync(
            new PythonScriptApprovalRequest("nightly.py", "1234"), TestContext.Current.CancellationToken);
        var folded = Assert.Single(reapproved.Scripts);
        Assert.Equal("Nightly.py", folded.Name);
        Assert.Equal(PythonScriptService.StatusApproved, folded.Status);

        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonResult
            {
                ExitCode = 0,
                StandardOutput = "",
                StandardError = "",
                RunTime = TimeSpan.FromMilliseconds(1),
                Executable = "python",
                CommandLine = "python Nightly.py"
            });
        var run = await service.RunAsync("nightly.py", TestContext.Current.CancellationToken);
        Assert.Equal(entry.Id, run.Name);

        var revoked = await service.RevokeAsync(
            new PythonScriptApprovalRequest("nightly.py", "1234"), TestContext.Current.CancellationToken);
        Assert.Equal(PythonScriptService.StatusUnapproved, Assert.Single(revoked.Scripts).Status);
    }

    [Fact]
    public async Task ConcurrentRunPinSettingsFromSeparateInstancesKeepBothScopesAndApprovals()
    {
        var (first, _) = await SignedService("good.py", "print('good')\n");
        var second = NewService(out _);
        await Task.WhenAll(
            Task.Run(() => first.SetRunPinRequirementAsync(
                new PythonScriptRunPinRequirementRequest(null, true, "1234"),
                TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => second.SetRunPinRequirementAsync(
                new PythonScriptRunPinRequirementRequest("good.py", true, "1234"),
                TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        var reopened = NewService(out _);
        var state = await reopened.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(state.RequirePinEachRun);
        var script = Assert.Single(state.Scripts);
        Assert.True(script.RequirePinEachRun);
        Assert.Equal(PythonScriptService.StatusApproved, script.Status);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => reopened.ValidateRunAuthorizationAsync(
            "good.py", null, TestContext.Current.CancellationToken));
        await reopened.ValidateRunAuthorizationAsync("good.py", "1234", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunPinSettingsWaitForTheSharedWriteLockAndCancellationDoesNotChangeTheDocument()
    {
        var (service, _) = await SignedService("good.py", "print('good')\n");
        var signingPath = Path.Combine(_installDirectory, PythonScriptService.SigningFileName);
        var original = File.ReadAllBytes(signingPath);
        using var heldLock = new FileStream(signingPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var pending = service.SetRunPinRequirementAsync(
            new PythonScriptRunPinRequirementRequest(null, true, "1234"), cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(original, File.ReadAllBytes(signingPath));
    }

    [Fact]
    public async Task ConcurrentApprovalsFromSeparateServiceInstancesAreAllKept()
    {
        // Two instances over one install directory model the dashboard and a
        // `vb --sign-script` process racing on the signing document: every approval must
        // survive the read-modify-write, and no scratch file may be left behind.
        var first = NewService(out _);
        var second = NewService(out _);
        await first.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        var names = Enumerable.Range(0, 8).Select(index => $"job{index}.py").ToList();
        foreach (var name in names)
        {
            await WriteScriptAsync(name, $"print('{name}')\n");
        }

        await Task.WhenAll(names.Select((name, index) => (index % 2 == 0 ? first : second).ApproveAsync(
            new PythonScriptApprovalRequest(name, "1234"), TestContext.Current.CancellationToken)));

        var status = await NewService(out _).GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(names.Count, status.Scripts.Count);
        Assert.All(status.Scripts, script => Assert.Equal(PythonScriptService.StatusApproved, script.Status));
        Assert.Empty(Directory.GetFiles(_installDirectory, "*.tmp"));
    }

    [Fact]
    public async Task LockedSigningFileFailsClosedInsteadOfReportingEmptyState()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on mandatory file-sharing locks.");
        var service = NewService(out _);
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);

        // While another process holds the file, a read must NOT come back as "no PIN, no
        // approvals" -- that would silently drop every signature and skip the PIN check.
        using (new FileStream(
                   Path.Combine(_installDirectory, PythonScriptService.SigningFileName),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<PythonScriptValidationException>(
                () => service.GetStatusAsync(TestContext.Current.CancellationToken));
        }

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.PinConfigured);
    }

    [Fact]
    public async Task InterpreterLaunchFailureBecomesAnActionableValidationError()
    {
        var service = NewService(out var runner);
        await WriteScriptAsync("good.py", "print('ok')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("good.py", "1234"), TestContext.Current.CancellationToken);
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PythonExecutionException("python.exe not found"));

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("good.py", TestContext.Current.CancellationToken));

        Assert.Contains("Python could not be started", error.Message);
        Assert.Empty(service.GetRunHistory().Runs);
    }

    [Fact]
    public async Task CreateWritesTheFileUnsignedAndRefusesAnExistingName()
    {
        var service = NewService(out _);

        var created = await service.CreateAsync(
            new PythonScriptSaveRequest("fresh.py", "print('hi')\n"), TestContext.Current.CancellationToken);

        Assert.Equal("print('hi')\n", File.ReadAllText(ScriptPath("fresh.py")));
        var script = created.Scripts.Single(entry => entry.Name == "fresh.py");
        Assert.Equal(PythonScriptService.StatusUnapproved, script.Status);
        Assert.Equal(ScriptPath("fresh.py"), script.Path);

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.CreateAsync(
            new PythonScriptSaveRequest("fresh.py", "print('other')\n"), TestContext.Current.CancellationToken));
        Assert.Contains("already", error.Message);
        // The refused create must not have touched the original file.
        Assert.Equal("print('hi')\n", File.ReadAllText(ScriptPath("fresh.py")));
    }

    [Fact]
    public async Task CreateWritesUtf8WithoutABom()
    {
        var service = NewService(out _);

        await service.CreateAsync(
            new PythonScriptSaveRequest("accents.py", "print('café')\n"), TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(ScriptPath("accents.py"));
        Assert.NotEqual([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray());
        Assert.Equal("print('café')\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SavingAnEditClearsTheSignature_SavingIdenticalContentKeepsIt()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);
        var opened = await service.GetContentAsync("job.py", TestContext.Current.CancellationToken);

        // A save that changes nothing is not an edit: the canonical hash is unchanged.
        var unchanged = await service.SaveContentAsync(
            new PythonScriptSaveRequest("job.py", "print('one')\n", opened.Version),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            unchanged.State.Scripts.Single(entry => entry.Name == "job.py").Status);

        var edited = await service.SaveContentAsync(
            new PythonScriptSaveRequest("job.py", "print('two')\n", unchanged.Version),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusModified,
            edited.State.Scripts.Single(entry => entry.Name == "job.py").Status);
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("job.py", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveRejectsAStaleVersionAndNeverRecreatesADeletedFile()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('opened')\n");
        var opened = await service.GetContentAsync("job.py", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() =>
            service.SaveContentAsync(
                new PythonScriptSaveRequest("job.py", "print('blind')\n"),
                TestContext.Current.CancellationToken));
        Assert.Equal("print('opened')\n", File.ReadAllText(ScriptPath("job.py")));

        File.WriteAllText(ScriptPath("job.py"), "print('newer')\n");
        var stale = await Assert.ThrowsAsync<PythonScriptValidationException>(() =>
            service.SaveContentAsync(
                new PythonScriptSaveRequest("job.py", "print('stale')\n", opened.Version),
                TestContext.Current.CancellationToken));
        Assert.Contains("changed after it was opened", stale.Message);
        Assert.Equal("print('newer')\n", File.ReadAllText(ScriptPath("job.py")));

        File.Delete(ScriptPath("job.py"));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() =>
            service.SaveContentAsync(
                new PythonScriptSaveRequest("job.py", "print('resurrected')\n", opened.Version),
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ScriptPath("job.py")));
    }

    [Fact]
    public async Task CreateClearsAStaleApprovalBeforePublishingTheFile()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('approved')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);

        // Model an external deletion, which cannot update the signing document.
        File.Delete(ScriptPath("job.py"));
        var created = await service.CreateAsync(
            new PythonScriptSaveRequest(Assert.Single((await service.GetStatusAsync(TestContext.Current.CancellationToken)).Scripts).Id, "print('approved')\n"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            PythonScriptService.StatusUnapproved,
            created.Scripts.Single(entry => entry.Name == "job.py").Status);
    }

    [Fact]
    public async Task GetContentReturnsTheTextAndItsSigningStatus()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);

        var content = await service.GetContentAsync("job.py", TestContext.Current.CancellationToken);

        Assert.Equal(Assert.Single((await service.GetStatusAsync(TestContext.Current.CancellationToken)).Scripts).Id, content.Name);
        Assert.Equal("print('one')\n", content.Content);
        Assert.Equal(PythonScriptService.StatusApproved, content.Status);

        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.GetContentAsync("../escape.py", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.GetContentAsync("missing.py", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetContentStripsALeadingBomSoARoundTripDoesNotBreakTheSignature()
    {
        var service = NewService(out _);
        Directory.CreateDirectory(Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory));
        File.WriteAllBytes(
            ScriptPath("bom.py"),
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. System.Text.Encoding.UTF8.GetBytes("print('hi')\n")]);
        await service.ImportAsync(new PythonScriptImportRequest(ScriptPath("bom.py")), TestContext.Current.CancellationToken);
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("bom.py", "1234"), TestContext.Current.CancellationToken);

        var content = await service.GetContentAsync("bom.py", TestContext.Current.CancellationToken);
        Assert.Equal("print('hi')\n", content.Content);

        var saved = await service.SaveContentAsync(
            new PythonScriptSaveRequest("bom.py", content.Content, content.Version),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            saved.State.Scripts.Single(entry => entry.Name == "bom.py").Status);
    }

    [Fact]
    public async Task ImportRegistersTheSourceInPlaceWithoutACopy()
    {
        var service = NewService(out _);
        var source = Path.Combine(_installDirectory, "outside.py");
        Directory.CreateDirectory(_installDirectory);
        File.WriteAllText(source, "print('imported')\n");

        var imported = await service.ImportAsync(
            new PythonScriptImportRequest(source, null), TestContext.Current.CancellationToken);

        Assert.Equal(source, Assert.Single(imported.Scripts).Path);
        Assert.False(File.Exists(ScriptPath("outside.py")));
        Assert.True(File.Exists(source));
        Assert.Equal(
            PythonScriptService.StatusUnapproved,
            imported.Scripts.Single(entry => entry.Name == "outside.py").Status);

        var clash = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ImportAsync(
            new PythonScriptImportRequest(source, "outside.py"), TestContext.Current.CancellationToken));
        Assert.Contains("already registered", clash.Message);
    }

    [Fact]
    public async Task ImportRefusesMissingFilesAndAnythingThatCouldNeverBeSigned()
    {
        var service = NewService(out _);
        Directory.CreateDirectory(_installDirectory);
        var binary = Path.Combine(_installDirectory, "binary.py");
        File.WriteAllBytes(binary, [0xC3, 0x28, 0xA0]);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ImportAsync(
            new PythonScriptImportRequest(Path.Combine(_installDirectory, "nope.py"), null),
            TestContext.Current.CancellationToken));

        var invalid = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ImportAsync(
            new PythonScriptImportRequest(binary, null), TestContext.Current.CancellationToken));
        Assert.Contains("UTF-8", invalid.Message);
        Assert.False(File.Exists(ScriptPath("binary.py")));

        // A linked file keeps its real extension; an import cannot disguise a text file.
        var named = Path.Combine(_installDirectory, "notes.txt");
        File.WriteAllText(named, "print('x')\n");
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ImportAsync(
            new PythonScriptImportRequest(named, "disguised.py"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(@"\\server\share\script.py")]
    [InlineData("//server/share/script.py")]
    public async Task ImportRejectsNetworkAndDeviceStylePathsBeforeAccess(string sourcePath)
    {
        var service = NewService(out _);

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(() =>
            service.ImportAsync(
                new PythonScriptImportRequest(sourcePath, "copy.py"),
                TestContext.Current.CancellationToken));

        Assert.Contains("Network and device paths", error.Message);
        Assert.False(File.Exists(ScriptPath("copy.py")));
    }

    [Fact]
    public async Task LinkedScriptsAreNeverListedReadSavedSignedOrRun()
    {
        var service = NewService(out _);
        Directory.CreateDirectory(Path.Combine(
            _installDirectory, PythonScriptService.ScriptsSubdirectory));
        var outside = Path.Combine(_installDirectory, "outside.txt");
        File.WriteAllText(outside, "do not overwrite\n");
        var link = ScriptPath("linked.py");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This platform does not permit creating file symbolic links.");
        }

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(status.Scripts, script => script.Name == "linked.py");
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.GetContentAsync("linked.py", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.SaveContentAsync(
                new PythonScriptSaveRequest("linked.py", "overwrite\n", new string('0', 64)),
                TestContext.Current.CancellationToken));
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.ApproveAsync(
                new PythonScriptApprovalRequest("linked.py", "1234"),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("linked.py", TestContext.Current.CancellationToken));
        Assert.Equal("do not overwrite\n", File.ReadAllText(outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedStagingDirectoryCannotRedirectVerifiedCopies(bool interactive)
    {
        var service = NewService(out _);
        await WriteScriptAsync("good.py", "print('ok')\n");
        await service.SetPinAsync(new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(new PythonScriptApprovalRequest("good.py", "1234"), TestContext.Current.CancellationToken);
        var outside = Path.Combine(_installDirectory, "outside");
        Directory.CreateDirectory(outside);
        var link = ScriptPath(".vb-scripts");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This platform does not permit creating directory symbolic links.");
        }

        try
        {
            if (interactive)
                await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RunInteractiveAsync("good.py", TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RunAsync("good.py", TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task RenameMovesTheFileAndTakesTheOldApprovalWithIt()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);

        var renamed = await service.RenameAsync(
            new PythonScriptRenameRequest("job.py", "nightly.py"), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(ScriptPath("job.py")));
        Assert.Equal("print('one')\n", File.ReadAllText(ScriptPath("nightly.py")));
        // The name is part of the hash preimage, so the new name is unsigned...
        Assert.Equal(
            PythonScriptService.StatusUnapproved,
            renamed.Scripts.Single(entry => entry.Name == "nightly.py").Status);

        // ...and the old name cannot resurrect its approval by being recreated byte-for-byte.
        await WriteScriptAsync("job.py", "print('one')\n");
        var afterRecreate = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusUnapproved,
            afterRecreate.Scripts.Single(entry => entry.Name == "job.py").Status);
    }

    [Fact]
    public async Task RenameRefusesAnExistingTargetName()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await WriteScriptAsync("other.py", "print('two')\n");

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RenameAsync(
            new PythonScriptRenameRequest("job.py", "other.py"), TestContext.Current.CancellationToken));

        Assert.Contains("already registered", error.Message);
        Assert.Equal("print('one')\n", File.ReadAllText(ScriptPath("job.py")));
        Assert.Equal("print('two')\n", File.ReadAllText(ScriptPath("other.py")));
    }

    [Fact]
    public async Task RemoveRetainsTheFileAndForgetsItsApproval()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);

        var deleted = await service.DeleteAsync("job.py", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(ScriptPath("job.py")));
        Assert.Empty(deleted.Scripts);

        // Restoring the exact bytes must not restore the approval the user threw away.
        await WriteScriptAsync("job.py", "print('one')\n");
        var restored = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusUnapproved,
            restored.Scripts.Single(entry => entry.Name == "job.py").Status);
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("job.py", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteDoesNotTouchTheFileWhenTrustStateCannotBeLocked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on mandatory file-sharing locks.");
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);

        using var heldLock = new FileStream(
            Path.Combine(_installDirectory, PythonScriptService.SigningFileName + ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DeleteAsync("job.py", cancellation.Token));

        Assert.True(File.Exists(ScriptPath("job.py")));
        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            status.Scripts.Single(entry => entry.Name == "job.py").Status);
    }

    [Fact]
    public async Task DeletingAScriptThatIsAlreadyGoneSucceeds()
    {
        var service = NewService(out _);
        await WriteScriptAsync("job.py", "print('one')\n");

        await service.DeleteAsync("job.py", TestContext.Current.CancellationToken);
        var again = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Empty(again.Scripts);
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.DeleteAsync("../escape.py", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunPassesArgumentsAndStandardInputThroughToPython()
    {
        var (service, runner) = await SignedService("args.py", "print('ok')\n");

        IReadOnlyList<string>? executedArguments = null;
        string? executedStandardInput = null;
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                executedArguments = (IReadOnlyList<string>)invocation.Arguments[0];
                executedStandardInput = (string?)invocation.Arguments[1];
            }))
            .ReturnsAsync(Completed("ok"));

        await service.RunAsync(
            "args.py",
            ["--out", "report.csv", "50"],
            "piped text",
            TestContext.Current.CancellationToken);

        Assert.NotNull(executedArguments);
        // The script path stays argv[0]; the caller's tokens follow it in order.
        Assert.Equal(4, executedArguments.Count);
        Assert.Equal(new[] { "--out", "report.csv", "50" }, executedArguments.Skip(1));
        Assert.Equal("piped text", executedStandardInput);
    }

    [Fact]
    public async Task RunSendsNoStandardInputWhenNoneWasSupplied()
    {
        var (service, runner) = await SignedService("quiet.py", "print('ok')\n");

        string? executedStandardInput = "unset";
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
                executedStandardInput = (string?)invocation.Arguments[1]))
            .ReturnsAsync(Completed("ok"));

        await service.RunAsync("quiet.py", arguments: null, standardInput: "", TestContext.Current.CancellationToken);

        // An empty box must not open a pipe: a script blocking on stdin would hang the run.
        Assert.Null(executedStandardInput);
    }

    [Fact]
    public async Task RunRefusesOversizedArgumentsAndStandardInputBeforeLaunchingPython()
    {
        var (service, runner) = await SignedService("bounded.py", "print('ok')\n");

        var tooMany = Enumerable.Range(0, 65).Select(index => index.ToString()).ToList();
        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("bounded.py", tooMany, null, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync(
                "bounded.py", [new string('x', 8_001)], null, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync(
                "bounded.py", null, new string('x', 256_001), TestContext.Current.CancellationToken));

        // Rejected before the interpreter is touched at all.
        runner.Verify(
            item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    // The whole of stdout is a return value…
    [InlineData("{\"rows\": 2}", "{\"rows\": 2}")]
    [InlineData("  [1, 2]  ", "[1, 2]")]
    // …and so is the last line, so a script can log freely and still return something.
    [InlineData("scanning\ndone\n{\"rows\": 3}\n", "{\"rows\": 3}")]
    [InlineData("scanning\r\n{\"ok\": true}\r\n", "{\"ok\": true}")]
    // A scalar, prose, or broken JSON is output — not a return value.
    [InlineData("42", null)]
    [InlineData("\"done\"", null)]
    [InlineData("true", null)]
    [InlineData("all finished\n", null)]
    [InlineData("{oops", null)]
    [InlineData("{\"a\": 1}\ntrailing prose", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractReturnJsonTakesObjectsAndArrays(string? standardOutput, string? expected)
    {
        Assert.Equal(expected, PythonScriptService.ExtractReturnJson(standardOutput));
    }

    [Fact]
    public async Task RunReportsTheScriptsReturnValueAlongsideItsOutput()
    {
        var (service, runner) = await SignedService("report.py", "print('x')\n");
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Completed("scanning 3 files\n{\"rows\": 3, \"path\": \"out.csv\"}\n"));

        var result = await service.RunAsync("report.py", TestContext.Current.CancellationToken);

        Assert.Equal("{\"rows\": 3, \"path\": \"out.csv\"}", result.ReturnJson);
        // The log the script printed on its way there is still all there.
        Assert.Contains("scanning 3 files", result.StandardOutput);
    }

    private static PythonResult Completed(string standardOutput) => new()
    {
        ExitCode = 0,
        StandardOutput = standardOutput,
        StandardError = "",
        RunTime = TimeSpan.FromMilliseconds(9),
        Executable = "python",
        CommandLine = "python script.py"
    };

    /// <summary>A service whose <paramref name="name"/> is written, PIN-configured and signed.</summary>
    private async Task<(PythonScriptService Service, Mock<IPythonRunner> Runner)> SignedService(
        string name, string content)
    {
        var service = NewService(out var runner);
        await WriteScriptAsync(name, content);
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest(name, "1234"), TestContext.Current.CancellationToken);
        return (service, runner);
    }

    private PythonScriptService NewService(out Mock<IPythonRunner> runner)
    {
        runner = new Mock<IPythonRunner>(MockBehavior.Loose);
        runner.SetupGet(item => item.Options).Returns(new PythonRunnerOptions());
        var capturedRunner = runner.Object;
        return new PythonScriptService(
            capturedRunner,
            _installDirectory,
            runnerFactory: _ => capturedRunner);
    }

    private string ScriptPath(string name) =>
        Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory, name);

    private async Task WriteScriptAsync(string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory));
        File.WriteAllText(ScriptPath(name), content);
        if (new[] { ".py", ".ps1", ".sh" }.Contains(Path.GetExtension(name)))
        {
            var library = new PythonScriptService(installDirectory: _installDirectory);
            if (!(await library.GetStatusAsync(TestContext.Current.CancellationToken)).Scripts.Any(item => item.Path == ScriptPath(name)))
                await library.ImportAsync(new PythonScriptImportRequest(ScriptPath(name)), TestContext.Current.CancellationToken);
        }
    }
}
