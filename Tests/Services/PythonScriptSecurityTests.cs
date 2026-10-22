using System.Diagnostics;
using PyBridge;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Services.PythonScripts;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class PythonScriptSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vb-script-security", Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "install");
    private string Repo => Path.Combine(_root, "repo");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private PythonScriptService Service(TimeProvider? clock = null, Func<string?, bool>? presence = null) =>
        new(installDirectory: Install, projectRootProvider: () => Repo,
            firstPinPresenceVerifier: presence, timeProvider: clock);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<PythonScriptService> Configured(TimeProvider? clock = null)
    {
        var service = Service(clock, _ => true);
        await service.SetPinAsync(new(null, "1234"), Ct);
        await service.CreateAsync(new("job.py", "print(1)", RequirePinEachRun: true), Ct);
        await service.ApproveAsync(new("job.py", "1234"), Ct);
        return service;
    }

    [Fact]
    public async Task FirstPinFailsClosedAndRequiresFreshSingleUseDashboardProof()
    {
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => Service().SetPinAsync(new(null, "1234"), Ct));
        Assert.False(File.Exists(Path.Combine(Install, PythonScriptService.SigningFileName)));
        var presence = new SettingsPresenceService();
        var settings = new Settings();
        var service = Service(presence: proof => presence.Verify(proof, settings));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SetPinAsync(new(null, "1234", "invented"), Ct));
        var code = presence.Challenge(false).Code;
        Assert.True((await service.SetPinAsync(new(null, "1234", code), Ct)).PinConfigured);
        Assert.False(presence.Verify(code, settings));
        // A fresh presence code cannot replace the existing signing PIN.
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SetPinAsync(new(null, "5678", presence.Challenge(false).Code), Ct));
        await service.SetPinAsync(new("1234", "5678"), Ct);
    }

    [Fact]
    public async Task FirstPinUsesRemoteAccessPinWhenConfigured()
    {
        var presence = new SettingsPresenceService();
        var code = presence.Challenge(false).Code;
        var (salt, hash) = Hasher.Hash("4567");
        var settings = new Settings { PinSalt = salt, PinHash = hash };
        var service = Service(presence: proof => presence.Verify(proof, settings));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SetPinAsync(new(null, "1234", code), Ct));
        Assert.True((await service.SetPinAsync(new(null, "1234", "4567"), Ct)).PinConfigured);
    }

    [Fact]
    public async Task ConcurrentGuessesShareFiveFailuresAcrossInstancesAndCooldownExpires()
    {
        var clock = new ManualClock();
        await Configured(clock);
        var guesses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            await Assert.ThrowsAsync<PythonScriptValidationException>(() =>
                Service(clock).ValidateRunAuthorizationAsync("job.py", "0000", Ct)), Ct)));
        Assert.Equal(5, guesses.Count(ex => !ex.Message.Contains("Too many")));
        Assert.Equal(15, guesses.Count(ex => ex.Message.Contains("Too many")));
        var service = Service(clock);
        var locked = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.PrepareTerminalRunAsync("job.py", "1234", Ct));
        Assert.Contains("Too many", locked.Message);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.ValidateRunAuthorizationAsync("job.py", "1234", Ct));
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.ValidateRunAuthorizationAsync("job.py", "1234", Ct);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("revoke")]
    [InlineData("change-pin")]
    [InlineData("run-pin")]
    [InlineData("settings")]
    [InlineData("save")]
    [InlineData("rename")]
    public async Task EveryPinOperationContributesToTheSameLockout(string operation)
    {
        var service = await Configured();
        var external = Path.Combine(_root, "profile.ps1");
        File.WriteAllText(external, "Write-Output 1");
        await service.ImportAsync(new(external), Ct);
        var version = (await service.GetContentAsync("profile.ps1", Ct)).Version;
        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<PythonScriptValidationException>(() => Service().ApproveAsync(new("job.py", "0000"), Ct));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => operation switch
        {
            "approve" => service.ApproveAsync(new("job.py", "0000"), Ct),
            "revoke" => service.RevokeAsync(new("job.py", "0000"), Ct),
            "change-pin" => service.SetPinAsync(new("0000", "5678"), Ct),
            "run-pin" => service.SetRunPinRequirementAsync(new(null, true, "0000"), Ct),
            "settings" => service.UpdateSettingsAsync(new("job.py", "Job", "global", false, "0000"), Ct),
            "save" => (Task)service.SaveContentAsync(new("profile.ps1", "changed", version, Pin: "0000"), Ct),
            _ => service.RenameAsync(new("profile.ps1", "other.ps1", "0000"), Ct)
        });
        var locked = await Assert.ThrowsAsync<PythonScriptValidationException>(() => Service().ApproveAsync(new("job.py", "1234"), Ct));
        Assert.Contains("Too many", locked.Message);
        Assert.Equal("Write-Output 1", File.ReadAllText(external));
    }

    [Fact]
    public async Task SuccessfulPinCheckResetsFailures()
    {
        var service = await Configured();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            for (var i = 0; i < 4; i++)
                await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RevokeAsync(new("job.py", "0000"), Ct));
            await Service().ApproveAsync(new("job.py", "1234"), Ct);
        }
    }

    [Theory]
    [InlineData("profile.ps1")]
    [InlineData("usercustomize.py")]
    [InlineData("startup.sh")]
    public async Task ExternalCreateRestoreSaveAndRenameCannotWriteWithoutPin(string name)
    {
        var service = Service();
        var outside = Path.Combine(_root, "outside");
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.CreateAsync(new(name, "attack", Directory: outside), Ct));
        Assert.False(Directory.Exists(outside));
        // Prefix siblings and traversal do not satisfy the repository/managed boundary.
        foreach (var directory in new[] { Repo + "-other", Path.Combine(Repo, "..", "outside"), service.GetScriptsDirectory() + "-other" })
            await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.CreateAsync(new(name, "attack", Directory: directory), Ct));
        Directory.CreateDirectory(outside);
        var path = Path.Combine(outside, name);
        File.WriteAllText(path, "original");
        var entry = Assert.Single((await service.ImportAsync(new(path), Ct)).Scripts);
        Assert.True(entry.RequiresWritePin);
        var version = (await service.GetContentAsync(entry.Id, Ct)).Version;
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SaveContentAsync(new(entry.Id, "attack", version), Ct));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.RenameAsync(new(entry.Id, "renamed" + Path.GetExtension(name)), Ct));
        Assert.Equal("original", File.ReadAllText(path));
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.SetPinAsync(new(null, "1234", "proof"), Ct));
        await Service(presence: _ => true).SetPinAsync(new(null, "1234"), Ct);
        var saved = await service.SaveContentAsync(new(entry.Id, "authorized", version, Pin: "1234"), Ct);
        Assert.Equal("authorized", File.ReadAllText(path));
        Assert.Equal("unapproved", Assert.Single(saved.State.Scripts).Status);
        File.Delete(path);
        await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.CreateAsync(new(entry.Id, "attack", Pin: "1234"), Ct));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedAndCurrentRepositoryWritesRemainPinFree(bool managed)
    {
        var service = Service();
        var entry = Assert.Single((await service.CreateAsync(new("job.py", "one", Directory: managed ? null : Repo), Ct)).Scripts);
        Assert.False(entry.RequiresWritePin);
        var version = (await service.GetContentAsync(entry.Id, Ct)).Version;
        await service.SaveContentAsync(new(entry.Id, "two", version), Ct);
        await service.RenameAsync(new(entry.Id, "renamed.py"), Ct);
        var otherProject = new PythonScriptService(installDirectory: Install, projectRootProvider: () => Path.Combine(_root, "other"));
        Assert.Equal(!managed, Assert.Single((await otherProject.GetStatusAsync(Ct)).Scripts).RequiresWritePin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedCapturedAndTerminalRunsIgnoreHostileImportsAndKeepPyBridgeIo(bool terminal)
    {
        var python = PythonLocator.FindBest();
        Assert.SkipWhen(python is null, "Python is required for the isolation regression.");
        var options = python!.ToOptions();
        var cache = Path.Combine(Repo, ".vb-scripts");
        Directory.CreateDirectory(cache);
        var marker = Path.Combine(Repo, "injected");
        // Simulate tracked repository and cache files, including a permissive .gitignore.
        File.WriteAllText(Path.Combine(cache, ".gitignore"), "!*.py\n");
        foreach (var folder in new[] { Repo, cache })
            foreach (var name in new[] { "json.py", "sitecustomize.py", "usercustomize.py" })
                File.WriteAllText(Path.Combine(folder, name), "from pathlib import Path\nPath('injected').touch()\nraise RuntimeError('unsigned code ran')\n");
        options.EnvironmentVariables["PYTHONPATH"] = Repo + Path.PathSeparator + cache;
        options.EnvironmentVariables["PYTHONIOENCODING"] = "ascii";
        var service = new PythonScriptService(new PythonRunner(options), Install,
            projectRootProvider: () => Repo, firstPinPresenceVerifier: _ => true);
        await service.CreateAsync(new("job.py", "import json, sys\nassert sys.flags.isolated and sys.flags.no_user_site and sys.flags.ignore_environment\nprint(json.dumps({'ok': True}))\nprint(input(), flush=True)\n", Directory: Repo), Ct);
        await service.SetPinAsync(new(null, "1234"), Ct);
        await service.ApproveAsync(new("job.py", "1234"), Ct);
        if (terminal)
        {
            var start = await service.PrepareTerminalRunAsync("job.py", null, Ct);
            Assert.Equal(new[] { "-I", "-u", "-X", "utf8" }, start.ArgumentList.Take(4));
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = System.Text.Encoding.UTF8;
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteLineAsync("héllo 世界".AsMemory(), Ct);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            var error = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            Assert.Equal("", await error);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("héllo 世界", await output);
        }
        else
        {
            var result = await service.RunAsync("job.py", [], "héllo 世界\n", null, Ct);
            Assert.Equal("", result.StandardError);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("héllo 世界", result.StandardOutput);
        }
        Assert.False(File.Exists(marker));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
