using System.Collections.Immutable;

namespace VibeRails.Daemon;

/// <summary>Stable executable and platform registration settings for one current-user daemon.</summary>
public sealed record DaemonRegistration
{
    public DaemonRegistration(
        string applicationId,
        string displayName,
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        string dataDirectory,
        string? description = null,
        string? windowsTaskBaseName = null,
        string? systemdUnitBaseName = null,
        string? launchAgentLabelBase = null,
        DaemonLegacyRegistrations? legacyRegistrations = null)
    {
        ApplicationId = DaemonIdentifier.Normalize(applicationId, nameof(applicationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DisplayName = displayName.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? DisplayName : description.Trim();
        ExecutablePath = RequireAbsolutePath(executablePath, nameof(executablePath));
        Arguments = arguments.Select(ValidateArgument).ToImmutableArray();
        WorkingDirectory = RequireAbsolutePath(workingDirectory, nameof(workingDirectory));
        DataDirectory = RequireAbsolutePath(dataDirectory, nameof(dataDirectory));
        WindowsTaskBaseName = DaemonIdentifier.Normalize(
            windowsTaskBaseName ?? ApplicationId,
            nameof(windowsTaskBaseName));
        SystemdUnitBaseName = DaemonIdentifier.Normalize(
            systemdUnitBaseName ?? ApplicationId,
            nameof(systemdUnitBaseName)).ToLowerInvariant();
        LaunchAgentLabelBase = DaemonIdentifier.Normalize(
            launchAgentLabelBase ?? ApplicationId,
            nameof(launchAgentLabelBase)).ToLowerInvariant();
        LegacyRegistrations = legacyRegistrations ?? DaemonLegacyRegistrations.None;
    }

    public string ApplicationId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string ExecutablePath { get; }
    public ImmutableArray<string> Arguments { get; }
    public string WorkingDirectory { get; }
    public string DataDirectory { get; }
    public string WindowsTaskBaseName { get; }
    public string SystemdUnitBaseName { get; }
    public string LaunchAgentLabelBase { get; }
    public DaemonLegacyRegistrations LegacyRegistrations { get; }

    public ScopedDaemonRegistration ForCurrentUser(CurrentUserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var suffix = identity.ScopeKey;
        return new ScopedDaemonRegistration(
            this,
            identity,
            PipeName: $"{ApplicationId}-{suffix}-ipc",
            MutexName: DaemonInstanceGuard.BuildMutexName(ApplicationId, identity),
            WindowsTaskName: $"{WindowsTaskBaseName}-{suffix}",
            SystemdUnitName: $"{SystemdUnitBaseName}-{suffix}.service",
            LaunchAgentLabel: $"{LaunchAgentLabelBase}.{suffix}");
    }

    private static string RequireAbsolutePath(string value, string parameterName)
    {
        if (value.Any(char.IsControl))
            throw new ArgumentException("Paths cannot contain control characters.", parameterName);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("A fully qualified path is required.", parameterName);
        return Path.GetFullPath(value);
    }

    private static string ValidateArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(ch => ch is '\r' or '\n' or '\0'))
            throw new ArgumentException("Daemon arguments cannot contain NUL or line-break characters.", nameof(value));
        return value;
    }
}

public sealed record ScopedDaemonRegistration(
    DaemonRegistration Registration,
    CurrentUserIdentity Identity,
    string PipeName,
    string MutexName,
    string WindowsTaskName,
    string SystemdUnitName,
    string LaunchAgentLabel);

public sealed record DaemonLegacyRegistrations
{
    public static DaemonLegacyRegistrations None { get; } = new();

    public static DaemonLegacyRegistrations VibeRailsJobs { get; } = new(
        windowsTaskNames: ["VibeRailsJobs"],
        systemdUnitNames: ["viberails-jobs.timer", "viberails-jobs.service"],
        launchAgentLabels: ["com.viberails.jobs"]);

    public DaemonLegacyRegistrations(
        IEnumerable<string>? windowsTaskNames = null,
        IEnumerable<string>? systemdUnitNames = null,
        IEnumerable<string>? launchAgentLabels = null)
    {
        WindowsTaskNames = Normalize(windowsTaskNames);
        SystemdUnitNames = Normalize(systemdUnitNames);
        LaunchAgentLabels = Normalize(launchAgentLabels);
    }

    public ImmutableArray<string> WindowsTaskNames { get; }
    public ImmutableArray<string> SystemdUnitNames { get; }
    public ImmutableArray<string> LaunchAgentLabels { get; }

    private static ImmutableArray<string> Normalize(IEnumerable<string>? values) => values is null
        ? []
        : values.Select(value =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Any(char.IsControl))
                throw new ArgumentException("Legacy registration names cannot contain control characters.");
            return value.Trim();
        }).Distinct(StringComparer.Ordinal).ToImmutableArray();
}

internal static class DaemonIdentifier
{
    private const int MaximumLength = 80;

    public static string Normalize(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > MaximumLength || !IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(ch => !IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                $"Daemon identifiers must start with an ASCII letter or digit, be at most {MaximumLength} characters, " +
                "and contain only letters, digits, '.', '_' or '-'.",
                parameterName);
        }

        return normalized;
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
