using VibeRails.Services.Cli;
using Xunit;

namespace Tests.Services.Cli;

/// <summary>
/// Pure text assertions. The script is the contract between "the step passed its test" and "the
/// step ran at launch", so the exit-code capture, the sentinel write, and the directory guard are
/// pinned here rather than only being exercised by a live process.
/// </summary>
public class TerminalScriptBuilderTests
{
    private const string Sentinel = @"C:\Temp\viberails-step-abc.exit";
    private const string WorkDir = @"C:\source\my app";

    [Fact]
    public void PowerShell_CapturesDollarQuestionBeforeLastExitCode()
    {
        var script = TerminalScriptBuilder.BuildPowerShellScript(
            "npm install", WorkDir, null, Sentinel, pauseOnFailure: true, selfDeletePath: null);

        var okIndex = script.IndexOf("$__vbr_ok = $?", StringComparison.Ordinal);
        var codeIndex = script.IndexOf("$__vbr_code = $global:LASTEXITCODE", StringComparison.Ordinal);
        var commandIndex = script.IndexOf("npm install", StringComparison.Ordinal);

        // $? reflects only the LAST statement, so reading it after anything else — including the
        // $LASTEXITCODE read — reports that statement's success instead of the command's.
        Assert.True(commandIndex >= 0);
        Assert.True(okIndex > commandIndex, "$? must be captured after the command");
        Assert.True(okIndex < codeIndex, "$? must be captured before $LASTEXITCODE");
    }

    [Fact]
    public void PowerShell_FailedCmdletWithStaleExitCode_ResolvesToFailure()
    {
        var script = TerminalScriptBuilder.BuildPowerShellScript(
            "Get-Item missing", WorkDir, null, Sentinel, pauseOnFailure: false, selfDeletePath: null);

        // A failed cmdlet never touches $LASTEXITCODE. Without the -not $__vbr_ok branch a stale 0
        // from an earlier native command would report it as success.
        Assert.Contains("if (-not $__vbr_ok) {", script);
        Assert.Contains("$__vbr_exit = 1", script);
        // And LASTEXITCODE is cleared before the command so there is no stale value to inherit.
        Assert.Contains("$global:LASTEXITCODE = $null", script);
    }

    [Fact]
    public void PowerShell_WritesTheSentinelBeforePausing()
    {
        var script = TerminalScriptBuilder.BuildPowerShellScript(
            "npm install", WorkDir, null, Sentinel, pauseOnFailure: true, selfDeletePath: null);

        var sentinelWrite = script.IndexOf("Set-Content -LiteralPath $__vbr_sentinel", StringComparison.Ordinal);
        var pause = script.IndexOf("Read-Host", StringComparison.Ordinal);

        Assert.True(sentinelWrite >= 0);
        // The failure window holds itself open for reading; that must not stall the caller, which
        // already has the exit code from the sentinel.
        Assert.True(pause > sentinelWrite, "the sentinel must be written before the window pauses");
    }

    [Fact]
    public void PowerShell_OmitsTheSentinelWhenThereIsNone()
    {
        var script = TerminalScriptBuilder.BuildPowerShellScript(
            "npm install", WorkDir, null, sentinelPath: null, pauseOnFailure: false, selfDeletePath: null);

        Assert.DoesNotContain("__vbr_sentinel", script);
        Assert.Contains("exit $__vbr_exit", script);
    }

    [Fact]
    public void PowerShell_GuardsTheWorkingDirectoryAndEscapesQuotes()
    {
        var script = TerminalScriptBuilder.BuildPowerShellScript(
            "npm install", @"C:\it's here", null, Sentinel, pauseOnFailure: false, selfDeletePath: null);

        // Set-Location's failure is non-terminating by default, which would run the command in the
        // wrong directory instead of failing.
        Assert.Contains("-ErrorAction Stop", script);
        Assert.Contains($"exit {TerminalScriptBuilder.WorkingDirectoryUnavailableExitCode}", script);
        // PowerShell single-quote escaping is doubling, so an apostrophe must not end the literal.
        Assert.Contains(@"'C:\it''s here'", script);
    }

    [Fact]
    public void Posix_GuardsTheWorkingDirectoryWithACdOrExit()
    {
        var script = TerminalScriptBuilder.BuildPosixScript(
            "npm install", "/home/rob/my app", null, "/tmp/step.exit", pauseOnFailure: false, selfDeletePath: null);

        Assert.Contains("cd '/home/rob/my app' ||", script);
        Assert.Contains($"exit {TerminalScriptBuilder.WorkingDirectoryUnavailableExitCode}", script);
        // Even the cd failure writes the sentinel: on POSIX there is no process handle, so a
        // missing sentinel is indistinguishable from a hang.
        Assert.Contains($"printf '%s' '{TerminalScriptBuilder.WorkingDirectoryUnavailableExitCode}' > \"$__vbr_sentinel\"", script);
    }

    [Fact]
    public void Posix_CapturesTheCommandExitCodeImmediately()
    {
        var script = TerminalScriptBuilder.BuildPosixScript(
            "npm install", "/srv/app", null, "/tmp/step.exit", pauseOnFailure: true, selfDeletePath: null);

        var commandIndex = script.IndexOf("npm install", StringComparison.Ordinal);
        var captureIndex = script.IndexOf("__vbr_exit=$?", StringComparison.Ordinal);

        Assert.True(captureIndex > commandIndex);
        Assert.Contains("read -r __vbr_ignored", script);
        Assert.Contains("exit $__vbr_exit", script);
    }

    [Fact]
    public void EnvironmentVariables_AreBakedIntoTheScript()
    {
        // ProcessStartInfo cannot carry them: the visible-window path needs UseShellExecute=true,
        // which makes the Environment collection unusable.
        var windows = TerminalScriptBuilder.BuildPowerShellScript(
            "npm install",
            WorkDir,
            new Dictionary<string, string?> { ["NODE_ENV"] = "prod", ["OLD"] = null },
            Sentinel,
            pauseOnFailure: false,
            selfDeletePath: null);

        Assert.Contains("$env:NODE_ENV = 'prod'", windows);
        Assert.Contains(@"Remove-Item -LiteralPath 'Env:\OLD'", windows);

        var posix = TerminalScriptBuilder.BuildPosixScript(
            "npm install",
            "/srv/app",
            new Dictionary<string, string?> { ["NODE_ENV"] = "prod", ["OLD"] = null },
            "/tmp/step.exit",
            pauseOnFailure: false,
            selfDeletePath: null);

        Assert.Contains("export NODE_ENV='prod'", posix);
        Assert.Contains("unset OLD", posix);
    }

    [Theory]
    [InlineData("NODE_ENV", true)]
    [InlineData("_private", true)]
    [InlineData("A1", true)]
    [InlineData("1BAD", false)]
    [InlineData("HAS SPACE", false)]
    [InlineData("HAS-DASH", false)]
    [InlineData("", false)]
    public void EnvironmentVariableNames_AreRestrictedRatherThanEscaped(string name, bool expected)
    {
        // Names are emitted unquoted, so anything outside the portable charset is dropped rather
        // than interpolated into the script.
        Assert.Equal(expected, TerminalScriptBuilder.IsValidEnvironmentVariableName(name));
    }

    [Fact]
    public void RejectedEnvironmentVariableNames_NeverReachTheScript()
    {
        var script = TerminalScriptBuilder.BuildPowerShellScript(
            "echo hi",
            WorkDir,
            new Dictionary<string, string?> { ["OK; Remove-Item C:\\"] = "x" },
            Sentinel,
            pauseOnFailure: false,
            selfDeletePath: null);

        Assert.DoesNotContain("Remove-Item C:\\", script);
    }

    [Fact]
    public void SelfDeleteLine_TargetsTheScriptsOwnPath()
    {
        // The step's window can outlive this process, so the script deletes itself on the way in
        // rather than relying on us to clean up after it.
        var windows = TerminalScriptBuilder.BuildPowerShellScript(
            "echo hi", WorkDir, null, Sentinel, pauseOnFailure: false, selfDeletePath: @"C:\Temp\step.ps1");
        Assert.Contains(@"Remove-Item -LiteralPath 'C:\Temp\step.ps1'", windows);

        var posix = TerminalScriptBuilder.BuildPosixScript(
            "echo hi", "/srv/app", null, "/tmp/step.exit", pauseOnFailure: false, selfDeletePath: "/tmp/step.sh");
        Assert.Contains("rm -f -- '/tmp/step.sh'", posix);
    }

    [Fact]
    public void PosixSourceCommand_DotSourcesTheQuotedPath()
    {
        // Dot-sourcing avoids depending on the execute bit and lets the script's own `exit` close
        // the terminal window.
        Assert.Equal(". '/tmp/my step.sh'", TerminalScriptBuilder.BuildPosixSourceCommand("/tmp/my step.sh"));
    }
}
