using System.Security;
using System.Text;
using System.Xml.Linq;

namespace VibeRails.Daemon.Platform;

/// <summary>Pure platform definition and quoting functions used by lifecycle managers and tests.</summary>
public static class DaemonServiceDefinitionRenderer
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string RenderWindowsTaskXml(ScopedDaemonRegistration scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        var sid = scoped.Identity.WindowsSid
            ?? throw new ArgumentException("A Windows SID is required to render a scheduled task.", nameof(scoped));
        var registration = scoped.Registration;
        var arguments = BuildWindowsCommandLine(registration.Arguments);

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(TaskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Description", registration.Description)),
                new XElement(TaskNamespace + "Triggers",
                    new XElement(TaskNamespace + "LogonTrigger",
                        new XElement(TaskNamespace + "Enabled", "true"),
                        new XElement(TaskNamespace + "UserId", sid))),
                new XElement(TaskNamespace + "Principals",
                    new XElement(TaskNamespace + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNamespace + "UserId", sid),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNamespace + "AllowHardTerminate", "true"),
                    new XElement(TaskNamespace + "StartWhenAvailable", "true"),
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNamespace + "IdleSettings",
                        new XElement(TaskNamespace + "StopOnIdleEnd", "false"),
                        new XElement(TaskNamespace + "RestartOnIdle", "false")),
                    new XElement(TaskNamespace + "AllowStartOnDemand", "true"),
                    new XElement(TaskNamespace + "Enabled", "true"),
                    new XElement(TaskNamespace + "Hidden", "false"),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNamespace + "WakeToRun", "false"),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNamespace + "Priority", "7"),
                    new XElement(TaskNamespace + "RestartOnFailure",
                        // The task schema's minInclusive for this interval is PT1M; a smaller
                        // value makes schtasks /Create reject the whole definition.
                        new XElement(TaskNamespace + "Interval", "PT1M"),
                        new XElement(TaskNamespace + "Count", "999"))),
                new XElement(TaskNamespace + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", registration.ExecutablePath),
                        new XElement(TaskNamespace + "Arguments", arguments),
                        new XElement(TaskNamespace + "WorkingDirectory", registration.WorkingDirectory)))));

        return document.Declaration + Environment.NewLine + document.Root;
    }

    public static bool IsCurrentWindowsTaskXml(string xml, ScopedDaemonRegistration scoped)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var root = document.Root;
            if (root?.Name != TaskNamespace + "Task")
                return false;

            string? First(string localName) => document
                .Descendants(TaskNamespace + localName)
                .FirstOrDefault()?.Value;
            string? DirectValue(XElement parent, string localName) => parent
                .Elements(TaskNamespace + localName)
                .SingleOrDefault()?.Value;

            var actions = root.Elements(TaskNamespace + "Actions").SingleOrDefault();
            var actionContext = actions?.Attribute("Context")?.Value;
            var principals = root.Elements(TaskNamespace + "Principals").SingleOrDefault();
            var selectedPrincipal = string.IsNullOrWhiteSpace(actionContext)
                ? null
                : principals?.Elements(TaskNamespace + "Principal")
                    .SingleOrDefault(element => string.Equals(
                        element.Attribute("id")?.Value,
                        actionContext,
                        StringComparison.Ordinal));
            var logonTrigger = document.Descendants(TaskNamespace + "LogonTrigger").FirstOrDefault();
            var restart = document.Descendants(TaskNamespace + "RestartOnFailure").FirstOrDefault();
            var expectedArguments = BuildWindowsCommandLine(scoped.Registration.Arguments);
            var expectedSid = scoped.Identity.WindowsSid;

            return string.Equals(First("Command"), scoped.Registration.ExecutablePath, PathComparison())
                   && string.Equals(First("Arguments") ?? string.Empty, expectedArguments, StringComparison.Ordinal)
                   && string.Equals(First("WorkingDirectory"), scoped.Registration.WorkingDirectory, PathComparison())
                   && !string.IsNullOrWhiteSpace(expectedSid)
                   && selectedPrincipal is not null
                   && string.Equals(
                       DirectValue(selectedPrincipal, "UserId"),
                       expectedSid,
                       StringComparison.OrdinalIgnoreCase)
                   && string.Equals(
                       DirectValue(selectedPrincipal, "LogonType"),
                       "InteractiveToken",
                       StringComparison.OrdinalIgnoreCase)
                   && string.Equals(
                       DirectValue(selectedPrincipal, "RunLevel"),
                       "LeastPrivilege",
                       StringComparison.OrdinalIgnoreCase)
                   && string.Equals(First("MultipleInstancesPolicy"), "IgnoreNew", StringComparison.OrdinalIgnoreCase)
                   && logonTrigger is not null
                   && restart is not null;
        }
        catch
        {
            return false;
        }
    }

    public static string RenderSystemdUserUnit(ScopedDaemonRegistration scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        var registration = scoped.Registration;
        var command = string.Join(' ', new[] { registration.ExecutablePath }
            .Concat(registration.Arguments)
            .Select(QuoteSystemdArgument));

        return $"""
            [Unit]
            Description={EscapeSystemdDescription(registration.Description)}

            [Service]
            Type=simple
            ExecStart={command}
            WorkingDirectory={EscapeSystemdPathSetting(registration.WorkingDirectory)}
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """ + Environment.NewLine;
    }

    public static bool IsCurrentSystemdUserUnit(string unit, ScopedDaemonRegistration scoped) =>
        NormalizeLines(unit) == NormalizeLines(RenderSystemdUserUnit(scoped));

    public static string RenderLaunchAgentPlist(ScopedDaemonRegistration scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        var registration = scoped.Registration;
        var arguments = new[] { registration.ExecutablePath }.Concat(registration.Arguments);
        var standardOut = Path.Combine(registration.DataDirectory, "logs", "vbd-launchd.out.log");
        var standardError = Path.Combine(registration.DataDirectory, "logs", "vbd-launchd.err.log");

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(
                "plist",
                "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd",
                null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    KeyValue("Label", new XElement("string", scoped.LaunchAgentLabel)),
                    KeyValue("ProgramArguments", new XElement("array",
                        arguments.Select(argument => new XElement("string", argument)))),
                    KeyValue("WorkingDirectory", new XElement("string", registration.WorkingDirectory)),
                    KeyValue("RunAtLoad", new XElement("true")),
                    KeyValue("KeepAlive", new XElement("true")),
                    KeyValue("ProcessType", new XElement("string", "Background")),
                    KeyValue("StandardOutPath", new XElement("string", standardOut)),
                    KeyValue("StandardErrorPath", new XElement("string", standardError)))));

        return document.Declaration + Environment.NewLine + document.DocumentType + Environment.NewLine + document.Root;
    }

    public static bool IsCurrentLaunchAgentPlist(string plist, ScopedDaemonRegistration scoped)
    {
        try
        {
            var document = XDocument.Parse(plist);
            var dictionary = document.Root?.Element("dict");
            if (dictionary is null)
                return false;

            var values = ReadPlistDictionary(dictionary);
            if (!values.TryGetValue("Label", out var label) || label.Value != scoped.LaunchAgentLabel)
                return false;
            if (!values.TryGetValue("WorkingDirectory", out var workDir) ||
                !string.Equals(workDir.Value, scoped.Registration.WorkingDirectory, PathComparison()))
                return false;
            if (!values.ContainsKey("RunAtLoad") || values["RunAtLoad"].Name.LocalName != "true" ||
                !values.ContainsKey("KeepAlive") || values["KeepAlive"].Name.LocalName != "true")
                return false;
            if (!values.TryGetValue("ProgramArguments", out var argumentArray))
                return false;

            var actualArguments = argumentArray.Elements("string").Select(element => element.Value);
            var expectedArguments = new[] { scoped.Registration.ExecutablePath }
                .Concat(scoped.Registration.Arguments);
            return actualArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string BuildWindowsCommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(QuoteWindowsArgument));
    }

    public static string QuoteWindowsArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    public static string QuoteSystemdArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
            throw new ArgumentException("systemd arguments cannot contain control characters.", nameof(value));
        return '"' + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal) + '"';
    }

    private static IEnumerable<object> KeyValue(string key, XElement value)
    {
        yield return new XElement("key", key);
        yield return value;
    }

    private static Dictionary<string, XElement> ReadPlistDictionary(XElement dictionary)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var elements = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < elements.Length; index += 2)
        {
            if (elements[index].Name.LocalName == "key")
                result[elements[index].Value] = elements[index + 1];
        }
        return result;
    }

    private static string EscapeSystemdDescription(string value) =>
        value.ReplaceLineEndings(" ").Replace("%", "%%", StringComparison.Ordinal);

    /// <summary>
    /// Path settings such as WorkingDirectory= are not command lines: systemd takes the value
    /// literally (quotes are NOT stripped, so a quoted path is rejected as non-absolute) and
    /// expands only % specifiers. Escape specifiers and emit the path unquoted.
    /// </summary>
    public static string EscapeSystemdPathSetting(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
            throw new ArgumentException("systemd path settings cannot contain control characters.", nameof(value));
        return value.Replace("%", "%%", StringComparison.Ordinal);
    }

    private static string NormalizeLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
