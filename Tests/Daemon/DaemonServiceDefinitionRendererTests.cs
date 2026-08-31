using System.Xml.Linq;
using VibeRails.Daemon;
using VibeRails.Daemon.Platform;
using Xunit;

namespace Tests.Daemon;

public sealed class DaemonServiceDefinitionRendererTests
{
    [Fact]
    public void WindowsTaskXml_RoundTripsCurrentRegistrationAndRejectsDrift()
    {
        var root = Path.Combine(Path.GetTempPath(), "vbd renderer & windows");
        var registration = DaemonRegistrationTests.Registration(root);
        var identity = new CurrentUserIdentity(
            "sid:S-1-5-21-4242",
            root,
            windowsSid: "S-1-5-21-4242");
        var scoped = registration.ForCurrentUser(identity);

        var xml = DaemonServiceDefinitionRenderer.RenderWindowsTaskXml(scoped);
        var document = XDocument.Parse(xml);

        Assert.True(DaemonServiceDefinitionRenderer.IsCurrentWindowsTaskXml(xml, scoped));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "LogonType" && element.Value == "InteractiveToken");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "RunLevel" && element.Value == "LeastPrivilege");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "MultipleInstancesPolicy" && element.Value == "IgnoreNew");
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RestartOnFailure");
        // The task schema's minInclusive for the restart interval is PT1M; anything smaller makes
        // schtasks /Create reject the whole definition on every Windows machine.
        Assert.Equal(
            "PT1M",
            document.Descendants()
                .Single(element => element.Name.LocalName == "RestartOnFailure")
                .Elements()
                .Single(element => element.Name.LocalName == "Interval")
                .Value);

        var staleDocument = XDocument.Parse(xml);
        staleDocument.Descendants()
            .Single(element => element.Name.LocalName == "Command")
            .Value = Path.Combine(root, "bin", "old-vb.exe");
        var stale = staleDocument.ToString();
        Assert.False(DaemonServiceDefinitionRenderer.IsCurrentWindowsTaskXml(stale, scoped));
        Assert.False(DaemonServiceDefinitionRenderer.IsCurrentWindowsTaskXml("not xml", scoped));
    }

    [Theory]
    [InlineData("S-1-5-21-9999", "InteractiveToken", "LeastPrivilege")]
    [InlineData("S-1-5-21-4242", "Password", "LeastPrivilege")]
    [InlineData("S-1-5-21-4242", "InteractiveToken", "HighestAvailable")]
    public void WindowsTaskXml_RejectsDriftOnPrincipalSelectedByActionsContext(
        string selectedSid,
        string selectedLogonType,
        string selectedRunLevel)
    {
        var root = Path.Combine(Path.GetTempPath(), "vbd renderer selected principal");
        var registration = DaemonRegistrationTests.Registration(root);
        var identity = new CurrentUserIdentity(
            "sid:S-1-5-21-4242",
            root,
            windowsSid: "S-1-5-21-4242");
        var scoped = registration.ForCurrentUser(identity);
        var document = XDocument.Parse(
            DaemonServiceDefinitionRenderer.RenderWindowsTaskXml(scoped));
        var taskNamespace = document.Root!.Name.Namespace;
        var principals = document.Descendants(taskNamespace + "Principals").Single();
        var actions = document.Descendants(taskNamespace + "Actions").Single();

        principals.Add(
            new XElement(taskNamespace + "Principal",
                new XAttribute("id", "SelectedByActions"),
                new XElement(taskNamespace + "UserId", selectedSid),
                new XElement(taskNamespace + "LogonType", selectedLogonType),
                new XElement(taskNamespace + "RunLevel", selectedRunLevel)));
        actions.SetAttributeValue("Context", "SelectedByActions");

        Assert.False(DaemonServiceDefinitionRenderer.IsCurrentWindowsTaskXml(
            document.ToString(),
            scoped));
    }

    [Fact]
    public void SystemdUserUnit_RoundTripsStructuredArgumentsAndRejectsDrift()
    {
        var registration = DaemonRegistrationTests.Registration(
            Path.Combine(Path.GetTempPath(), "vbd renderer systemd"));
        var scoped = registration.ForCurrentUser(DaemonRegistrationTests.Identity());

        var unit = DaemonServiceDefinitionRenderer.RenderSystemdUserUnit(scoped);

        Assert.True(DaemonServiceDefinitionRenderer.IsCurrentSystemdUserUnit(unit, scoped));
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("RestartSec=5", unit, StringComparison.Ordinal);
        Assert.Contains("\"two words\"", unit, StringComparison.Ordinal);
        // WorkingDirectory= is a path setting, not a command line: systemd takes it literally and
        // rejects a quoted value as non-absolute ("Working directory path is not absolute,
        // ignoring"), silently running the daemon in the user manager's default cwd instead.
        Assert.Contains(
            $"WorkingDirectory={scoped.Registration.WorkingDirectory}\n",
            unit.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.False(DaemonServiceDefinitionRenderer.IsCurrentSystemdUserUnit(
            unit.Replace("Restart=on-failure", "Restart=no", StringComparison.Ordinal),
            scoped));
    }

    [Fact]
    public void SystemdPathSetting_EscapesSpecifiersAndStaysUnquoted()
    {
        Assert.Equal(
            "/home/user/100%%done",
            DaemonServiceDefinitionRenderer.EscapeSystemdPathSetting("/home/user/100%done"));
        Assert.Throws<ArgumentException>(() =>
            DaemonServiceDefinitionRenderer.EscapeSystemdPathSetting("line1\nline2"));
    }

    [Fact]
    public void LaunchAgentPlist_RoundTripsArgumentArrayAndRejectsDrift()
    {
        var registration = DaemonRegistrationTests.Registration(
            Path.Combine(Path.GetTempPath(), "vbd renderer launchagent"));
        var scoped = registration.ForCurrentUser(DaemonRegistrationTests.Identity());

        var plist = DaemonServiceDefinitionRenderer.RenderLaunchAgentPlist(scoped);
        var document = XDocument.Parse(plist);

        Assert.True(DaemonServiceDefinitionRenderer.IsCurrentLaunchAgentPlist(plist, scoped));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "key" && element.Value == "RunAtLoad");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "key" && element.Value == "KeepAlive");
        Assert.Contains(document.Descendants("string"), element => element.Value == "two words");

        var stale = plist.Replace(
            scoped.LaunchAgentLabel,
            scoped.LaunchAgentLabel + ".old",
            StringComparison.Ordinal);
        Assert.False(DaemonServiceDefinitionRenderer.IsCurrentLaunchAgentPlist(stale, scoped));
        Assert.False(DaemonServiceDefinitionRenderer.IsCurrentLaunchAgentPlist("not xml", scoped));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\Program Files\\VibeRails\\", "\"C:\\Program Files\\VibeRails\\\\\"")]
    public void QuoteWindowsArgument_HandlesSpacesQuotesAndTrailingBackslashes(
        string value,
        string expected)
    {
        Assert.Equal(expected, DaemonServiceDefinitionRenderer.QuoteWindowsArgument(value));
    }

    [Fact]
    public void BuildWindowsCommandLine_QuotesEveryStructuredArgument()
    {
        var commandLine = DaemonServiceDefinitionRenderer.BuildWindowsCommandLine(
            ["--job-daemon", "two words", "C:\\tail\\"]);

        Assert.Equal("\"--job-daemon\" \"two words\" \"C:\\tail\\\\\"", commandLine);
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("$HOME/%i/\"quoted\"\\tail", "\"$$HOME/%%i/\\\"quoted\\\"\\\\tail\"")]
    public void QuoteSystemdArgument_EscapesSpecifiersVariablesQuotesAndBackslashes(
        string value,
        string expected)
    {
        Assert.Equal(expected, DaemonServiceDefinitionRenderer.QuoteSystemdArgument(value));
    }

    [Fact]
    public void QuoteSystemdArgument_RejectsControlCharacters()
    {
        Assert.Throws<ArgumentException>(() =>
            DaemonServiceDefinitionRenderer.QuoteSystemdArgument("line1\nline2"));
    }
}
