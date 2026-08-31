using VibeRails.Daemon;
using Xunit;

namespace Tests.Daemon;

public sealed class DaemonRegistrationTests
{
    [Fact]
    public void CurrentUserIdentity_UsesStableNonDisclosingScopeKey()
    {
        var profile = Path.Combine(Path.GetTempPath(), "vbd-profile");

        var identity = new CurrentUserIdentity(
            "sid:S-1-5-21-1234",
            profile,
            windowsSid: "S-1-5-21-1234");

        Assert.Equal("dae81fe86f1c4da3", identity.ScopeKey);
        Assert.Equal(identity.ScopeKey, CurrentUserIdentity.CreateScopeKey(identity.StableId));
        Assert.DoesNotContain("S-1-5-21", identity.ScopeKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(profile), identity.UserProfileDirectory);
    }

    [Fact]
    public void ForCurrentUser_ScopesEveryCrossProcessNameToIdentity()
    {
        var firstIdentity = Identity("uid:1000", 1000);
        var secondIdentity = Identity("uid:1001", 1001);
        var registration = Registration();

        var first = registration.ForCurrentUser(firstIdentity);
        var second = registration.ForCurrentUser(secondIdentity);

        Assert.Equal($"viberails-job-daemon-{firstIdentity.ScopeKey}-ipc", first.PipeName);
        Assert.Equal(
            DaemonInstanceGuard.BuildMutexName("viberails-job-daemon", firstIdentity),
            first.MutexName);
        Assert.Equal($"VibeRailsJobDaemon-{firstIdentity.ScopeKey}", first.WindowsTaskName);
        Assert.Equal($"viberails-job-daemon-{firstIdentity.ScopeKey}.service", first.SystemdUnitName);
        Assert.Equal($"com.viberails.jobdaemon.{firstIdentity.ScopeKey}", first.LaunchAgentLabel);

        Assert.NotEqual(first.PipeName, second.PipeName);
        Assert.NotEqual(first.MutexName, second.MutexName);
        Assert.NotEqual(first.WindowsTaskName, second.WindowsTaskName);
        Assert.NotEqual(first.SystemdUnitName, second.SystemdUnitName);
        Assert.NotEqual(first.LaunchAgentLabel, second.LaunchAgentLabel);
    }

    [Fact]
    public void VibeRailsJobsLegacyPreset_ContainsExactHistoricalNames()
    {
        var legacy = DaemonLegacyRegistrations.VibeRailsJobs;

        Assert.Equal(["VibeRailsJobs"], legacy.WindowsTaskNames.ToArray());
        Assert.Equal(
            ["viberails-jobs.timer", "viberails-jobs.service"],
            legacy.SystemdUnitNames.ToArray());
        Assert.Equal(["com.viberails.jobs"], legacy.LaunchAgentLabels.ToArray());
    }

    [Fact]
    public void Registration_PreservesStructuredArgumentsAndNormalizesPlatformNames()
    {
        var registration = Registration();

        Assert.Equal(["--job-daemon", "two words", "quoted\"value"], registration.Arguments.ToArray());
        Assert.Equal("VibeRailsJobDaemon", registration.WindowsTaskBaseName);
        Assert.Equal("viberails-job-daemon", registration.SystemdUnitBaseName);
        Assert.Equal("com.viberails.jobdaemon", registration.LaunchAgentLabelBase);
        Assert.Same(DaemonLegacyRegistrations.VibeRailsJobs, registration.LegacyRegistrations);
    }

    internal static DaemonRegistration Registration(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "vbd-registration-tests");
        return new DaemonRegistration(
            applicationId: "viberails-job-daemon",
            displayName: "VibeRails Demon",
            executablePath: Path.Combine(root, "bin", "vb.exe"),
            arguments: ["--job-daemon", "two words", "quoted\"value"],
            workingDirectory: root,
            dataDirectory: Path.Combine(root, "data"),
            description: "VibeRails background Automations",
            windowsTaskBaseName: "VibeRailsJobDaemon",
            systemdUnitBaseName: "VibeRails-Job-Daemon",
            launchAgentLabelBase: "Com.VibeRails.JobDaemon",
            legacyRegistrations: DaemonLegacyRegistrations.VibeRailsJobs);
    }

    internal static CurrentUserIdentity Identity(string stableId = "uid:1000", uint uid = 1000) =>
        new(stableId, Path.Combine(Path.GetTempPath(), "vbd-profile"), unixUserId: uid);
}
