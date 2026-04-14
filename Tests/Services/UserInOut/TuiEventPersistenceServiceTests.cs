using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DB;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.UserInOut;

public class TuiEventPersistenceServiceTests
{
    [Fact]
    public async Task StartAsync_PersistsWatchedEvents()
    {
        var sessionId = Guid.NewGuid().ToString();
        var persisted = new TaskCompletionSource<(string SessionId, string TriggerString, TUI_Event_Watcher_Type EventType)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.InsertTuiEventAsync(
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<TUI_Event_Watcher_Type>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(r => r.InsertTuiEventAsync(
                sessionId,
                It.IsAny<DateTimeOffset>(),
                "\x1B",
                TUI_Event_Watcher_Type.Escape,
                It.IsAny<CancellationToken>()))
            .Callback<string, DateTimeOffset, string, TUI_Event_Watcher_Type, CancellationToken>((sid, _, trigger, type, _) =>
                persisted.TrySetResult((sid, trigger, type)))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        using var serviceProvider = services.BuildServiceProvider();

        var sut = new TuiEventPersistenceService(serviceProvider.GetRequiredService<IServiceScopeFactory>());
        await sut.StartAsync(CancellationToken.None);

        try
        {
            TUI_Event_Watcher.Watch(sessionId, "\x1B");

            var result = await persisted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(sessionId, result.SessionId);
            Assert.Equal("\x1B", result.TriggerString);
            Assert.Equal(TUI_Event_Watcher_Type.Escape, result.EventType);

            repository.Verify(r => r.InsertTuiEventAsync(
                sessionId,
                It.IsAny<DateTimeOffset>(),
                "\x1B",
                TUI_Event_Watcher_Type.Escape,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
            TUI_Event_Watcher.ClearSession(sessionId);
        }
    }
}
