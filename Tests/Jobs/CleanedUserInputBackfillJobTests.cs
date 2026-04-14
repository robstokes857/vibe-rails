using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Jobs;
using VibeRails.Services;
using VibeRails.Services.CleanedInput;
using Xunit;

namespace Tests.Jobs;

public class CleanedUserInputBackfillJobTests
{
    private static UserInputRecord MakeRawInput(long id) => new(
        Id: id,
        SessionId: "session-1",
        Sequence: (int)id,
        InputText: $"input-{id}",
        GitCommitHash: null,
        TimestampUTC: DateTime.UtcNow);

    [Fact]
    public async Task ExecuteJob_CleansEachUncleanedRow()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.GetUncleanedInputsForClosedSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>
            {
                MakeRawInput(1),
                MakeRawInput(2),
                MakeRawInput(3),
            });
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());

        var cleanedSvc = new Mock<ICleanedUserInputService>();

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => repo.Object)
            .AddScoped(_ => cleanedSvc.Object)
            .BuildServiceProvider();

        var job = new CleanedUserInputBackfillJob(
            NullLogger<CleanedUserInputBackfillJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>());

        await InvokeExecuteJobAsync(job);

        cleanedSvc.Verify(x => x.CleanAndPersistAsync(1, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        cleanedSvc.Verify(x => x.CleanAndPersistAsync(2, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        cleanedSvc.Verify(x => x.CleanAndPersistAsync(3, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteJob_NoOpWhenNoUncleanedRows()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.GetUncleanedInputsForClosedSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>());
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());

        var cleanedSvc = new Mock<ICleanedUserInputService>();

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => repo.Object)
            .AddScoped(_ => cleanedSvc.Object)
            .BuildServiceProvider();

        var job = new CleanedUserInputBackfillJob(
            NullLogger<CleanedUserInputBackfillJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>());

        await InvokeExecuteJobAsync(job);

        cleanedSvc.Verify(
            x => x.CleanAndPersistAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteJob_ContinuesAfterIndividualFailure()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.GetUncleanedInputsForClosedSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>
            {
                MakeRawInput(1),
                MakeRawInput(2),
                MakeRawInput(3),
            });
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());

        var cleanedSvc = new Mock<ICleanedUserInputService>();
        cleanedSvc
            .Setup(x => x.CleanAndPersistAsync(2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => repo.Object)
            .AddScoped(_ => cleanedSvc.Object)
            .BuildServiceProvider();

        var job = new CleanedUserInputBackfillJob(
            NullLogger<CleanedUserInputBackfillJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>());

        await InvokeExecuteJobAsync(job);

        cleanedSvc.Verify(x => x.CleanAndPersistAsync(1, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        cleanedSvc.Verify(x => x.CleanAndPersistAsync(2, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        cleanedSvc.Verify(x => x.CleanAndPersistAsync(3, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task InvokeExecuteJobAsync(CleanedUserInputBackfillJob job)
    {
        var executeJob = typeof(CleanedUserInputBackfillJob).GetMethod(
            "ExecuteJob",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(executeJob);

        var task = executeJob!.Invoke(job, [CancellationToken.None]) as Task;
        Assert.NotNull(task);

        await task!;
    }
}
