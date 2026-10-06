using Moq;
using System.Diagnostics;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.VCA;

public sealed class RuleFilePathSecurityTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "viberails-rule-path-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly AgentFileService _service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public RuleFilePathSecurityTests()
    {
        _root = Path.Combine(_sandbox, "repo");
        Directory.CreateDirectory(_root);
        var git = new Mock<IGitService>();
        git.Setup(x => x.GetRootPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_root);
        _service = new AgentFileService(git.Object, new RulesService());
    }

    [Theory]
    [InlineData("../outside/vc.rules.md")]
    [InlineData("../repo-other/vc.rules.md")]
    [InlineData("notes.md")]
    [InlineData(".git/vc.rules.md")]
    public async Task EveryReadAndWriteRejectsPathsOutsideTheRuleFileBoundary(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = "## Vibe Rails Rules\n- [WARN] Log all file changes\n";
        await File.WriteAllTextAsync(path, original, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.ResolvePathAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetRulesAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetAgentFileContentAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetDocumentedFilesAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAgentFileAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddRulesAsync(path, Ct, "Log all file changes"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddRuleWithEnforcementAsync(path, "Log all file changes", Enforcement.STOP, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.DeleteRulesAsync(path, Ct, "Log all file changes"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateRuleEnforcementAsync(path, "Log all file changes", Enforcement.STOP, Ct));
        Assert.Equal(original, await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task NestedRuleFileCanBeCreatedAndEditedButNeverOverwrittenByCreate()
    {
        var directory = Path.Combine(_root, "src");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vc.rules.md");
        await _service.CreateAgentFileAsync(path, Ct, "Log all file changes");
        await _service.UpdateRuleEnforcementAsync(path, "Log all file changes", Enforcement.STOP, Ct);
        Assert.Equal(Enforcement.STOP, Assert.Single(await _service.GetRulesWithEnforcementAsync(path, Ct)).Enforcement);
        var content = await File.ReadAllTextAsync(path, Ct);
        await Assert.ThrowsAsync<IOException>(() => _service.CreateAgentFileAsync(path, Ct));
        Assert.Equal(content, await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task LinkedDirectoryCannotRedirectReadsOrCreates()
    {
        var outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) Assert.Skip($"Directory symlinks unavailable: {ex.GetType().Name}");
            // Windows junctions need no symlink privilege. Pass paths as environment data,
            // keeping the PowerShell program constant even when a temp path contains quotes.
            var info = new ProcessStartInfo(ShellDefaults.ResolveWindowsCommandShellPath())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    "New-Item -ItemType Junction -Path $env:VIBERAILS_TEST_LINK -Target $env:VIBERAILS_TEST_TARGET -ErrorAction Stop | Out-Null" }
            };
            info.Environment["VIBERAILS_TEST_LINK"] = link;
            info.Environment["VIBERAILS_TEST_TARGET"] = outside;
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            Assert.True(process.ExitCode == 0, await error);
        }
        try
        {
            var path = Path.Combine(link, "vc.rules.md");
            await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAgentFileAsync(path, Ct));
            Assert.False(File.Exists(Path.Combine(outside, "vc.rules.md")));
            await File.WriteAllTextAsync(Path.Combine(outside, "vc.rules.md"), "private", Ct);
            await Assert.ThrowsAsync<ArgumentException>(() => _service.GetRulesAsync(path, Ct));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task LinkedFileAndDanglingLinkAreRejected()
    {
        var target = Path.Combine(_sandbox, "outside.md");
        var link = Path.Combine(_root, "vc.rules.md");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Assert.Skip($"File symlinks unavailable: {ex.GetType().Name}"); }
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAgentFileAsync(link, Ct));
            await File.WriteAllTextAsync(target, "private", Ct);
            await Assert.ThrowsAsync<ArgumentException>(() => _service.GetRulesAsync(link, Ct));
        }
        finally { File.Delete(link); }
    }

    [Theory]
    [InlineData("vc.rules.md")]
    [InlineData("C:vc.rules.md")]
    [InlineData("\\\\server\\share\\vc.rules.md")]
    [InlineData("\\\\?\\C:\\repo\\vc.rules.md")]
    public void RelativeAndNetworkOrDevicePathsAreRejected(string path)
        => Assert.Throws<ArgumentException>(() => RuleFilePath.Resolve(_root, path));

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);
}
