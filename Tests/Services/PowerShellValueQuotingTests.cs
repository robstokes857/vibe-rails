using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using VibeRails.Services;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Mcp.HostShell;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

/// <summary>
/// Every builder that writes PowerShell text from a value (VIBE-153). PowerShell ends a
/// single-quoted string at U+2018–U+201B as well as at the ASCII apostrophe, and a double-quoted
/// one at U+201C–U+201E as well as at the ASCII quote, so escaping only the ASCII character lets a
/// legal path or argument run code. Each case runs the generated text through real pwsh and asserts
/// the value arrives exactly and the payload never runs. BaseLlmCliLauncher has its own suite,
/// WindowsLaunchScriptTests.
/// </summary>
public sealed class PowerShellValueQuotingTests
{
    // A legal Windows file name that breaks out of a single-quoted literal at ’ and out of a
    // double-quoted one at ”. Either breakout runs Write-Host, which prints INJECTED on a line of
    // its own; the value only ever carries it inside other text.
    private const string Hostile = "a\u2019 + $(Write-Host INJECTED) + \u2018b \u201D; Write-Host INJECTED; \u201Cc [1] it's";

    // Values are printed as UTF-8 Base64 so the assertions never depend on a console code page.
    private const string Print = "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes({0}))";

    [Fact]
    public async Task DecodedLiteral_IsTheExactValueAsAnExpressionAndAsAnArgument()
    {
        var pwsh = FindPwsh();
        var value = Hostile + "\r\nline two \U0001F600 \0 end";
        using var dir = new TempDirectory();
        var echo = WriteEchoScript(dir.Path);

        var result = await RunScriptAsync(pwsh,
            $"'VAL:' + {string.Format(Print, ShellArgSanitizer.DecodedPowerShellLiteral(value))}\n" +
            $"& {ShellArgSanitizer.DecodedPowerShellLiteral(echo)} {ShellArgSanitizer.DecodedPowerShellLiteral(value)}");

        result.AssertClean();
        Assert.Equal([value], result.Values("VAL:"));
        Assert.Equal([value], result.Values("ARG:"));
    }

    [Fact]
    public async Task EscapeArg_KeepsCurlyDoubleQuotesInsideTheArgument()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory();
        var echo = WriteEchoScript(dir.Path);

        // The interactive launch line CommandService types into a tab: EscapeArg per argument.
        var result = await RunScriptAsync(pwsh,
            $"& {ShellArgSanitizer.BuildSafeArgString([pwsh, "-NoProfile", "-NonInteractive", "-File", echo, Hostile, "--model=x"])}");

        result.AssertClean();
        Assert.Equal([Hostile, "--model=x"], result.Values("ARG:"));
    }

    [Fact]
    public async Task JobProcessLauncher_ScriptPassesArgvIntact()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory(Hostile);
        var echo = WriteEchoScript(dir.Path);

        var result = await RunScriptAsync(pwsh, JobProcessLauncher.BuildWindowsScript(
            pwsh, ["-NoProfile", "-NonInteractive", "-File", echo, "--workdir", Hostile], dir.Path)
            + "\n'DIR:' + " + string.Format(Print, "(Get-Location).ProviderPath"));

        result.AssertClean();
        Assert.Equal(["--workdir", Hostile], result.Values("ARG:"));
        Assert.Equal(Path.TrimEndingDirectorySeparator(dir.Path), Assert.Single(result.Values("DIR:")), ignoreCase: true);
    }

    [Fact]
    public async Task JobTerminalTabLauncher_RunCommandPassesArgvIntact()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory(Hostile);
        var echo = WriteEchoScript(dir.Path);

        var result = await RunScriptAsync(pwsh, JobTerminalTabLauncher.BuildRunCommand(
            pwsh, ["-NoProfile", "-NonInteractive", "-File", echo, "--workdir", Hostile], windows: true));

        result.AssertClean();
        Assert.Equal(["--workdir", Hostile], result.Values("ARG:"));
    }

    [Fact]
    public async Task CliSpawnCommandBuilder_ScriptPassesArgvIntact()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory(Hostile);
        var echo = WriteEchoScript(dir.Path);

        var (_, argv) = CliSpawnCommandBuilder.BuildWindows(
            pwsh, ["-NoProfile", "-NonInteractive", "-File", echo, "--prompt", Hostile], []);
        try
        {
            var result = await RunAsync(pwsh, ["-NoProfile", "-NonInteractive", .. argv]);

            result.AssertClean();
            Assert.Equal(["--prompt", Hostile], result.Values("ARG:"));
        }
        finally { File.Delete(argv[^1]); }
    }

    [Fact]
    public async Task PythonScriptRunCommand_PassesTheExecutableAndNameIntact()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory(Hostile);
        // The command calls the executable directly, so a script stands in for vb.
        var echo = WriteEchoScript(dir.Path);

        var result = await RunScriptAsync(pwsh, TerminalTabHostService.BuildPythonScriptRunCommand(echo, Hostile));

        result.AssertClean();
        Assert.Equal(Hostile, result.Values("ARG:")[^1]);
    }

    [Fact]
    public async Task TerminalScriptBuilder_EntersTheDirectoryAndKeepsEveryValue()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory(Hostile);
        var sentinel = Path.Combine(dir.Path, Hostile + ".exit");
        var selfDelete = Path.Combine(dir.Path, Hostile + ".ps1");
        File.WriteAllText(selfDelete, "");

        var script = TerminalScriptBuilder.BuildPowerShellScript(
            $"'DIR:' + {string.Format(Print, "(Get-Location).ProviderPath")}\n" +
            $"'ENV:' + {string.Format(Print, "$env:VB_QUOTING_TEST")}",
            dir.Path,
            new Dictionary<string, string?> { ["VB_QUOTING_TEST"] = Hostile },
            sentinel,
            pauseOnFailure: false,
            selfDeletePath: selfDelete);
        var result = await RunScriptAsync(pwsh, script);

        result.AssertClean();
        Assert.Equal(Path.TrimEndingDirectorySeparator(dir.Path), Assert.Single(result.Values("DIR:")), ignoreCase: true);
        Assert.Equal([Hostile], result.Values("ENV:"));
        Assert.Equal("0", File.ReadAllText(sentinel));
        Assert.False(File.Exists(selfDelete), "the script deletes the path it is given");
    }

    [Fact]
    public async Task HostShellCommandService_RunsInTheRequestedDirectory()
    {
        FindPwsh();
        using var dir = new TempDirectory(Hostile);
        await using var service = new HostShellCommandService(NullLogger<HostShellCommandService>.Instance);

        var result = await service.RunAsync(new HostShellCommandRequest(
            Command: $"'DIR:' + {string.Format(Print, "(Get-Location).ProviderPath")}",
            WorkingDirectory: dir.Path,
            TimeoutSeconds: 20,
            WaitSeconds: 30),
            TestContext.Current.CancellationToken);

        Assert.Equal(HostShellCommandStatus.Completed, result.Status);
        Assert.Equal(0, result.ExitCode);
        var output = new PwshResult(0, result.Stdout + result.Stderr);
        output.AssertClean();
        Assert.Equal(Path.TrimEndingDirectorySeparator(dir.Path), Assert.Single(output.Values("DIR:")), ignoreCase: true);
    }

    [Fact]
    public async Task ShellService_PassesArgvIntact()
    {
        var pwsh = FindPwsh();
        using var dir = new TempDirectory(Hostile);
        var echo = WriteEchoScript(dir.Path);

        var stdout = await new ShellService(NullLogger<ShellService>.Instance).RunAsync(
            TestContext.Current.CancellationToken, pwsh, "-NoProfile", "-NonInteractive", "-File", echo, Hostile);

        var result = new PwshResult(0, stdout);
        result.AssertClean();
        Assert.Equal([Hostile], result.Values("ARG:"));
    }

    [Fact]
    public void NoSourceQuotesPowerShellByDoublingTheApostrophe()
    {
        // The pattern behind every site above. Values go through DecodedPowerShellLiteral.
        var source = Path.Combine(FindRepositoryRoot(), "VibeRails");
        var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), """Replace\(\s*"'"\s*,\s*"''"\s*"""))
            .Select(path => Path.GetRelativePath(source, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string FindPwsh()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "These builders emit PowerShell on Windows only.");
        string? pwsh = null;
        try { pwsh = ShellDefaults.ResolveWindowsCommandShellPath(); }
        catch (InvalidOperationException) { }
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) is not on PATH.");
        return pwsh!;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>A script that prints each argument it receives as ARG:&lt;UTF-8 Base64&gt;.</summary>
    private static string WriteEchoScript(string directory)
    {
        var path = Path.Combine(directory, "echo.ps1");
        File.WriteAllText(path, "$args | ForEach-Object { 'ARG:' + " + string.Format(Print, "$_") + " }\n", new UTF8Encoding(false));
        return path;
    }

    private static async Task<PwshResult> RunScriptAsync(string pwsh, string scriptText)
    {
        var script = Path.Combine(Path.GetTempPath(), $"viberails-quoting-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, scriptText, new UTF8Encoding(false));
        try { return await RunAsync(pwsh, ["-NoProfile", "-NonInteractive", "-File", script]); }
        finally { File.Delete(script); }
    }

    private static async Task<PwshResult> RunAsync(string pwsh, IEnumerable<string> arguments)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var startInfo = new ProcessStartInfo(pwsh)
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new PwshResult(process.ExitCode, await stdout + await stderr);
    }

    private sealed record PwshResult(int ExitCode, string Output)
    {
        private string[] Lines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public void AssertClean()
        {
            Assert.True(ExitCode == 0, Output);
            // The value carries "INJECTED" as text; only a payload that ran prints it alone.
            Assert.DoesNotContain("INJECTED", Lines);
        }

        public string[] Values(string prefix) => Lines
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line[prefix.Length..])))
            .ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string? name = null) =>
            Path = Directory.CreateTempSubdirectory(name is null ? "viberails-quoting-" : name + " ").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
