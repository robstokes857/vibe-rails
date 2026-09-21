using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardLaunchConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-launch-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly Mock<ITerminalTabHostService> _tabs = new(MockBehavior.Strict);
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardLaunchConcurrencyTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString, _connectionString);
        _tabs.SetupGet(t => t.MaxTabs).Returns(8);
        _tabs.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TerminalTabStatusResponse("tab-1", DateTime.UtcNow, false));
        _tabs.Setup(t => t.DeleteTabAsync("tab-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task ConcurrentLaunchesAcrossServiceInstancesOnlyStartOneCli()
    {
        var card = await CreateCardAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TerminalStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(finish.Task);
        var first = new BoardLaunchService(_store, _repository.Object, _tabs.Object).LaunchAsync(_root, card.Id, null, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try
        {
            await Assert.ThrowsAsync<BoardConflictException>(() =>
                new BoardLaunchService(_store, _repository.Object, _tabs.Object).LaunchAsync(_root, card.Key, null, Ct));
        }
        finally { finish.TrySetResult(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root)); }
        Assert.NotNull(await first);
        _tabs.Verify(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single((await _store.GetCardDetailAsync(_root, card.Id, Ct))!.Sessions);
    }

    [Fact]
    public async Task FailedLaunchReleasesReservationSoRetryCanStart()
    {
        var card = await CreateCardAsync();
        _tabs.SetupSequence(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("failed startup"))
            .ReturnsAsync(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root));
        var service = new BoardLaunchService(_store, _repository.Object, _tabs.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchAsync(_root, card.Id, null, Ct));
        Assert.NotNull(await service.LaunchAsync(_root, card.Key, null, Ct));
        _tabs.Verify(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private async Task<BoardCardRecord> CreateCardAsync()
    {
        await _store.EnsureDefaultColumnsAsync(_root, Ct);
        return await _store.CreateCardAsync(_root, new(null, "Concurrent card", "Scope", "base:codex", "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task CardDeletedDuringStartupClosesTheUnlinkedTerminal()
    {
        var card = await CreateCardAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TerminalStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(finish.Task);
        var launch = new BoardLaunchService(_store, _repository.Object, _tabs.Object).LaunchAsync(_root, card.Id, null, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try { Assert.True(await _store.DeleteCardAsync(_root, card.Id, Ct)); }
        finally { finish.TrySetResult(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "codex", _root)); }
        await Assert.ThrowsAsync<BoardValidationException>(() => launch);
        _tabs.Verify(t => t.DeleteTabAsync("tab-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
