using VibeRails.Services.PythonScripts;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services;

public sealed class PythonScriptRunProcessHostTests
{
    [Theory]
    [InlineData(new[] { "--run-python-script", "prompt.py" }, 0)]
    [InlineData(new[] { "--vs-code-v1", "--run-python-script", "prompt.py" }, 1)]
    [InlineData(new[] { "--", "--run-python-script", "prompt.py" }, -1)]
    public void HelperFlagIsRecognizedOnlyBeforeTheArgumentSeparator(string[] args, int expected)
    {
        Assert.Equal(expected, PythonScriptRunProcessHost.FindFlagIndex(args));
        Assert.Equal(expected >= 0, PythonScriptRunProcessHost.IsRequested(args));
    }

    [Fact]
    public void TerminalCommandQuotesTheExecutableAndScriptNameForTheHostShell()
    {
        var command = TerminalTabHostService.BuildPythonScriptRunCommand(
            OperatingSystem.IsWindows() ? @"C:\Program Files\Vibe Rails\vb.exe" : "/opt/vibe rails/vb",
            "ask me.py");

        Assert.Contains(PythonScriptRunProcessHost.Flag, command, StringComparison.Ordinal);
        Assert.Contains("ask me.py", command, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
            Assert.Equal("& 'C:\\Program Files\\Vibe Rails\\vb.exe' --run-python-script 'ask me.py'", command);
        else
            Assert.Equal("'/opt/vibe rails/vb' --run-python-script 'ask me.py'", command);
    }
}
