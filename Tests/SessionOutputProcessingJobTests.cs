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
        var firstInputTime = new DateTime(2026, 03, 20, 18, 00, 00, DateTimeKind.Utc);
        var secondInputTime = firstInputTime.AddMinutes(5);
        var dbService = new Mock<IDbService>();
        dbService
            .Setup(x => x.GetEndedUnprocessedSessionIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "session-1" });
        dbService
            .Setup(x => x.GetSessionLogChunksAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>
            {
                new(1, firstInputTime.AddMinutes(-1), Array.Empty<byte>()),
                new(2, firstInputTime, Array.Empty<byte>()),
                new(3, firstInputTime.AddMinutes(1), Array.Empty<byte>()),
                new(4, secondInputTime.AddMinutes(1), Array.Empty<byte>())
            });
        dbService
            .Setup(x => x.GetUserInputsForSessionAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>
            {
                new(1, "session-1", 1, "First user input", null, firstInputTime),
                new(2, "session-1", 2, "Second user input", null, secondInputTime)
            });

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => dbService.Object)
            .BuildServiceProvider();

        var parser = new Mock<ISessionOutputParser>();
        parser
            .Setup(x => x.ParseAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken _) =>
            {
                var ids = string.Join(",", chunks.Select(chunk => chunk.Id));
                return ids switch
                {
                    "1" => "Preamble text",
                    "2,3" => "Assistant response one",
                    "4" => "Assistant response two",
                    _ => string.Empty
                };
            });

        var job = new SessionOutputProcessingJob(
            NullLogger<SessionOutputProcessingJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            parser.Object);

        await InvokeExecuteJobAsync(job);

        var expectedTranscript = """
            Assistant said:
            Preamble text

            >>>>>>>>> Message 1
            User said:
            First user input
            <<<<<<<<< End Message 1
            Assistant said:
            Assistant response one

            >>>>>>>>> Message 2
            User said:
            Second user input
            <<<<<<<<< End Message 2
            Assistant said:
            Assistant response two
            """.ReplaceLineEndings("\n").TrimEnd().Replace("\n", Environment.NewLine, StringComparison.Ordinal);

        dbService.Verify(x => x.SaveSessionOutputAndMarkProcessedAsync("session-1", expectedTranscript, It.IsAny<CancellationToken>()), Times.Once);
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
            .ReturnsAsync(new List<SessionLogChunkRecord> { new(1, DateTime.UtcNow, Array.Empty<byte>()) });
        dbService
            .Setup(x => x.GetUserInputsForSessionAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>());

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

    [Fact]
    public async Task ExecuteJob_KeepsAssistantOnlyTextWhenNoUserInputsExist()
    {
        var dbService = new Mock<IDbService>();
        dbService
            .Setup(x => x.GetEndedUnprocessedSessionIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "session-1" });
        dbService
            .Setup(x => x.GetSessionLogChunksAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>
            {
                new(1, DateTime.UtcNow, Array.Empty<byte>())
            });
        dbService
            .Setup(x => x.GetUserInputsForSessionAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>());

        using var serviceProvider = new ServiceCollection()
            .AddScoped(_ => dbService.Object)
            .BuildServiceProvider();

        var parser = new Mock<ISessionOutputParser>();
        parser
            .Setup(x => x.ParseAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("assistant only text");

        var job = new SessionOutputProcessingJob(
            NullLogger<SessionOutputProcessingJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            parser.Object);

        await InvokeExecuteJobAsync(job);

        dbService.Verify(x => x.SaveSessionOutputAndMarkProcessedAsync("session-1", "assistant only text", It.IsAny<CancellationToken>()), Times.Once);
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
