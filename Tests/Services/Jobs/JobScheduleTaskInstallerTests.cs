using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobScheduleTaskInstallerTests
{
    [Fact]
    public void BuildWindowsTaskCommand_UsesRealQuotesForPathsContainingSpaces()
    {
        var command = JobScheduleTaskInstaller.BuildWindowsTaskCommand(
            @"C:\Program Files\dotnet\dotnet.exe",
            [@"C:\source code\VibeRails.dll", "--job-tick"]);

        Assert.Equal(
            @"""C:\Program Files\dotnet\dotnet.exe"" ""C:\source code\VibeRails.dll"" --job-tick",
            command);
        Assert.DoesNotContain("\\\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWindowsTaskCommand_PreservesTrailingBackslashesInQuotedArguments()
    {
        var command = JobScheduleTaskInstaller.BuildWindowsTaskCommand(
            "vb.exe",
            [@"C:\directory with spaces\", "--job-tick"]);

        Assert.Equal(
            "vb.exe \"C:\\directory with spaces\\\\\" --job-tick",
            command);
    }

    /// <summary>
    /// systemd expands both <c>%</c> specifiers and <c>$VAR</c> references inside ExecStart, double
    /// quotes included. A path like /home/$USER/bin/vb is the realistic way this bites: unescaped,
    /// systemd substitutes an empty string and the unit points at a path that does not exist.
    /// </summary>
    [Theory]
    [InlineData("/usr/bin/vb", "\"/usr/bin/vb\"")]
    [InlineData("/home/$USER/bin/vb", "\"/home/$$USER/bin/vb\"")]
    [InlineData("/opt/100%/vb", "\"/opt/100%%/vb\"")]
    [InlineData("/opt/a\"b/vb", "\"/opt/a\\\"b/vb\"")]
    [InlineData(@"C:\tools\vb", "\"C:\\\\tools\\\\vb\"")]
    public void QuoteSystemdArgument_EscapesEverySystemdMetacharacter(string value, string expected)
    {
        Assert.Equal(expected, JobScheduleTaskInstaller.QuoteSystemdArgument(value));
    }

    [Fact]
    public void QuoteSystemdArgument_EscapesDollarBeforeBackslash()
    {
        // Ordering matters: escaping backslashes first would double the one this adds for a quote.
        Assert.Equal("\"$$a\\\\b\"", JobScheduleTaskInstaller.QuoteSystemdArgument(@"$a\b"));
    }

    /// <summary>
    /// The LaunchAgent plist is hand-built rather than serialized, so every argument that reaches a
    /// &lt;string&gt; node has to be escaped here or the plist is malformed and launchctl rejects it.
    /// </summary>
    [Theory]
    [InlineData("/usr/local/bin/vb", "/usr/local/bin/vb")]
    [InlineData("/opt/a&b/vb", "/opt/a&amp;b/vb")]
    [InlineData("/opt/<vb>", "/opt/&lt;vb&gt;")]
    [InlineData("a&b<c>d", "a&amp;b&lt;c&gt;d")]
    public void EscapeXml_EscapesPlistMetacharacters(string value, string expected)
    {
        Assert.Equal(expected, JobScheduleTaskInstaller.EscapeXml(value));
    }

    [Fact]
    public void EscapeXml_DoesNotDoubleEscapeItsOwnAmpersands()
    {
        // & must be replaced first, or the &lt; this produces becomes &amp;lt;.
        Assert.Equal("&lt;a&gt;", JobScheduleTaskInstaller.EscapeXml("<a>"));
    }
}
