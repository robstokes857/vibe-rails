using VibeRails.Services.PythonScripts;
using Xunit;

namespace Tests.Services;

public sealed class PythonScriptSignProcessHostTests : IDisposable
{
    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(), "vb-pyscript-sign-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_installDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData(new[] { "--sign-script", "x.py" }, 0)]
    [InlineData(new[] { "--SIGN-SCRIPT", "x.py" }, 0)]
    [InlineData(new[] { "--env", "worker", "--sign-script", "x.py" }, 2)]
    // Everything after `--` is forwarded to the launched CLI verbatim and must never
    // switch vb into signing mode.
    [InlineData(new[] { "--env", "worker", "--", "--sign-script", "x.py" }, -1)]
    [InlineData(new[] { "--", "--sign-script" }, -1)]
    [InlineData(new[] { "mcp" }, -1)]
    [InlineData(new string[0], -1)]
    public void FindFlagIndex_OnlyMatchesTopLevelArgumentsBeforeTheSeparator(string[] args, int expected)
    {
        Assert.Equal(expected, PythonScriptSignProcessHost.FindFlagIndex(args));
        Assert.Equal(expected >= 0, PythonScriptSignProcessHost.IsRequested(args));
    }

    [Fact]
    public async Task RunAsync_RefusesToEnrolOrSignFromRedirectedStdin()
    {
        Assert.SkipUnless(Console.IsInputRedirected, "Only meaningful when stdin is not an interactive console.");
        Directory.CreateDirectory(Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory));
        File.WriteAllText(
            Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory, "auto.py"),
            "print('x')\n");

        var exitCode = await PythonScriptSignProcessHost.RunAsync(
            [PythonScriptSignProcessHost.Flag, "auto.py"], _installDirectory);

        // `echo pin | vb --sign-script auto.py` must not create a PIN or an approval.
        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDirectory, PythonScriptService.SigningFileName)));
    }

    [Fact]
    public async Task RunAsync_WithoutAScriptNamePrintsUsage()
    {
        Assert.Equal(1, await PythonScriptSignProcessHost.RunAsync(
            [PythonScriptSignProcessHost.Flag], _installDirectory));
        Assert.Equal(1, await PythonScriptSignProcessHost.RunAsync(
            [PythonScriptSignProcessHost.Flag, "--"], _installDirectory));
    }
}
