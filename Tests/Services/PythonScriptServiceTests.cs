using Moq;
using PyBridge;
using VibeRails.DTOs;
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
        WriteScript("job.py", "print('one')\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "wrong"), TestContext.Current.CancellationToken));

        var approved = await service.ApproveAsync(
            new PythonScriptApprovalRequest("job.py", "1234"), TestContext.Current.CancellationToken);
        Assert.Equal(
            PythonScriptService.StatusApproved,
            approved.Scripts.Single(script => script.Name == "job.py").Status);

        WriteScript("job.py", "print('two')\n");
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
        WriteScript("stable.py", "a = 1\nb = 2\n");
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
        WriteScript("danger.py", "print('x')\n");

        await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("danger.py", TestContext.Current.CancellationToken));

        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest("danger.py", "1234"), TestContext.Current.CancellationToken);
        WriteScript("danger.py", "print('tampered')\n");

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
    public async Task RunExecutesAVerifiedTempCopyAndRecordsHistory()
    {
        var service = NewService(out var runner);
        WriteScript("good.py", "print('ok')\n");
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
        // The exact verified bytes run from a temp copy, never the still-writable original.
        Assert.NotEqual(ScriptPath("good.py"), executedPath);
        Assert.Equal("good.py", Assert.Single(service.GetRunHistory().Runs).Name);
    }

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
        WriteScript("hello.py", "print('hello from viberails')\n");
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
        WriteScript("legacy.py", "x = 'a'\n");
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
        WriteScript("Nightly.py", "print(1)\n");
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
        Assert.Equal("Nightly.py", run.Name);

        var revoked = await service.RevokeAsync(
            new PythonScriptApprovalRequest("nightly.py", "1234"), TestContext.Current.CancellationToken);
        Assert.Equal(PythonScriptService.StatusUnapproved, Assert.Single(revoked.Scripts).Status);
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
            WriteScript(name, $"print('{name}')\n");
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
        WriteScript("good.py", "print('ok')\n");
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

    private void WriteScript(string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory));
        File.WriteAllText(ScriptPath(name), content);
    }
}
