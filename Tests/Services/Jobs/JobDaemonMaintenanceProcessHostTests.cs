using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobDaemonMaintenanceProcessHostTests
{
    [Fact]
    public void IsRequested_RecognizesSeparateAndInlineCommandForms()
    {
        Assert.True(JobDaemonMaintenanceProcessHost.IsRequested(
            ["vb", "--job-daemon-service", "status", "--json"]));
        Assert.True(JobDaemonMaintenanceProcessHost.IsRequested(
            ["--job-daemon-service=repair", "--json"]));
        Assert.True(JobDaemonMaintenanceProcessHost.IsRequested(
            ["--JOB-DAEMON-SERVICE", "STOP"]));
    }

    [Fact]
    public void IsRequested_DoesNotInspectArgumentsAfterTheOptionSentinel()
    {
        Assert.False(JobDaemonMaintenanceProcessHost.IsRequested(
            ["--", "--job-daemon-service", "status"]));
        Assert.False(JobDaemonMaintenanceProcessHost.IsRequested(
            ["--job-daemon", "status"]));
    }

    [Fact]
    public async Task RunAsync_UnknownCommandReturnsAUsageExitCodeWithoutLifecycleMutation()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await JobDaemonMaintenanceProcessHost.RunAsync(
            ["--job-daemon-service", "definitely-not-a-command", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Unknown VBD lifecycle command", error.ToString());
    }

    [Theory]
    [InlineData("--job-daemon-service")]
    [InlineData("--job-daemon-service --json")]
    public async Task RunAsync_MissingCommandReturnsTheAdvertisedUsage(string commandLine)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await JobDaemonMaintenanceProcessHost.RunAsync(
            commandLine.Split(' '),
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Usage: vb --job-daemon-service", error.ToString());
    }

    [Fact]
    public void JsonContract_IsCamelCaseAndIncludesNestedDaemonStatus()
    {
        var status = new JobDaemonStatusResponse(
            JobDaemonState.Running,
            "test-platform",
            IsSupported: true,
            IsInstalled: true,
            IsRunning: true,
            IsReachable: true,
            RegistrationIsCurrent: true,
            CurrentVersion: "1.2.3",
            DaemonVersion: "1.2.3",
            ProtocolVersion: 1,
            Pid: 42,
            StartedUtc: new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
            UptimeSeconds: 15,
            LastCycleUtc: new DateTime(2026, 8, 30, 12, 0, 10, DateTimeKind.Utc),
            OwnsSchedulerLease: true,
            AllowedActions: ["stop", "restart"]);
        var response = new JobDaemonActionResponse(true, "VibeRails Demon started.", status);

        var json = JsonSerializer.Serialize(
            response,
            AppJsonSerializerContext.Default.JobDaemonActionResponse);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VibeRails Demon started.", root.GetProperty("message").GetString());
        var serializedStatus = root.GetProperty("status");
        Assert.Equal("Running", serializedStatus.GetProperty("state").GetString());
        Assert.True(serializedStatus.GetProperty("isInstalled").GetBoolean());
        Assert.Equal(42, serializedStatus.GetProperty("pid").GetInt32());
        Assert.True(serializedStatus.GetProperty("ownsSchedulerLease").GetBoolean());
        Assert.Equal(
            ["stop", "restart"],
            serializedStatus.GetProperty("allowedActions")
                .EnumerateArray()
                .Select(element => element.GetString()!)
                .ToArray());
        Assert.False(root.TryGetProperty("Success", out _));
    }
}
