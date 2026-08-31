using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

public interface IJobDaemonLifecycleService
{
    Task<JobDaemonStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<JobDaemonActionResponse> InstallAsync(CancellationToken cancellationToken = default);
    Task<JobDaemonActionResponse> StartAsync(CancellationToken cancellationToken = default);
    Task<JobDaemonActionResponse> StopAsync(CancellationToken cancellationToken = default);
    Task<JobDaemonActionResponse> RestartAsync(CancellationToken cancellationToken = default);
    Task<JobDaemonActionResponse> RepairAsync(CancellationToken cancellationToken = default);
    Task<JobDaemonActionResponse> UninstallAsync(CancellationToken cancellationToken = default);
}
