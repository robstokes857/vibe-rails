using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Jobs;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class SessionOutputProcessingJobTests
{
    [Fact]
    public async Task ExecuteJob_ParsesAndPersistsEachEndedSession()
    {
        var dbService = new Mock<IDbService>();
        dbService
            .Setup(x => x.GetEndedUnprocessedSessionIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "session-1", "session-2" });
        dbService
            .Setup(x => x.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord> { new(1, Array.Empty<byte>()) });

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => dbService.Object)
            .BuildServiceProvider();

        var parser = new Mock<ISessionOutputParser>();
        parser
            .Setup(x => x.ParseAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("parsed text");

        var job = new SessionOutputProcessingJob(
            NullLogger<SessionOutputProcessingJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            parser.Object);

        await InvokeExecuteJobAsync(job);

        dbService.Verify(x => x.SaveSessionOutputAndMarkProcessedAsync("session-1", "parsed text", It.IsAny<CancellationToken>()), Times.Once);
        dbService.Verify(x => x.SaveSessionOutputAndMarkProcessedAsync("session-2", "parsed text", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteJob_LeavesSessionUnprocessedWhenParserFails()
    {
        var dbService = new Mock<IDbService>();
        dbService
            .Setup(x => x.GetEndedUnprocessedSessionIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "session-1" });
        dbService
            .Setup(x => x.GetSessionLogChunksAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord> { new(1, Array.Empty<byte>()) });

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => dbService.Object)
            .BuildServiceProvider();

        var parser = new Mock<ISessionOutputParser>();
        parser
            .Setup(x => x.ParseAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("parse failed"));

        var job = new SessionOutputProcessingJob(
            NullLogger<SessionOutputProcessingJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            parser.Object);

        await InvokeExecuteJobAsync(job);

        dbService.Verify(x => x.SaveSessionOutputAndMarkProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task InvokeExecuteJobAsync(SessionOutputProcessingJob job)
    {
        var executeJob = typeof(SessionOutputProcessingJob).GetMethod(
            "ExecuteJob",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(executeJob);

        var task = executeJob!.Invoke(job, [CancellationToken.None]) as Task;
        Assert.NotNull(task);

        await task!;
    }
}
