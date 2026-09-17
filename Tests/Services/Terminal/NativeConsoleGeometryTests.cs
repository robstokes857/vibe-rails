using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class NativeConsoleGeometryTests
{
    [Fact]
    public void TryCreateSize_ReturnsTheVisibleConsoleGrid()
    {
        var success = NativeConsoleGeometry.TryCreateSize(
            outputRedirected: false,
            cols: 211,
            rows: 47,
            out var size);

        Assert.True(success);
        Assert.Equal(new NativeConsoleSize(211, 47), size);
    }

    [Fact]
    public void TryCreateSize_RejectsRedirectedOutput()
    {
        var success = NativeConsoleGeometry.TryCreateSize(
            outputRedirected: true,
            cols: 211,
            rows: 47,
            out var size);

        Assert.False(success);
        Assert.Equal(default, size);
    }

    [Theory]
    [InlineData(9, 30)]
    [InlineData(1001, 30)]
    [InlineData(120, 4)]
    [InlineData(120, 501)]
    public void TryCreateSize_RejectsUnsupportedPtyDimensions(int cols, int rows)
    {
        Assert.False(NativeConsoleGeometry.TryCreateSize(
            outputRedirected: false,
            cols,
            rows,
            out _));
    }

    [Fact]
    public async Task TerminalStateService_RecordsInitialAndChangedNativeGeometry()
    {
        var repository = new Mock<IRepository>();
        repository
            .Setup(x => x.CreateSessionAsync(
                It.IsAny<string>(),
                "glm-5.2",
                "nightly",
                "C:\\source\\project",
                It.IsAny<int>(),
                "run-123"))
            .Returns(Task.CompletedTask);
        var persisted = new List<(string SessionId, TerminalOutputWrite Row)>();
        repository
            .Setup(x => x.PersistTerminalOutputAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<TerminalOutputWrite>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<TerminalOutputWrite>, CancellationToken>((id, rows, _) =>
            {
                lock (persisted)
                {
                    foreach (var row in rows)
                        persisted.Add((id, row));
                }
            })
            .Returns(Task.CompletedTask);
        repository
            .Setup(x => x.CompleteSessionAsync(It.IsAny<string>(), 0))
            .Returns(Task.CompletedTask);

        using var service = new TerminalStateService(
            repository.Object,
            Mock.Of<IGitService>(),
            Mock.Of<IRemoteStateService>(),
            Mock.Of<ITerminalIoObserverService>());

        var sessionId = await service.CreateSessionAsync(
            "glm-5.2",
            "C:\\source\\project",
            "nightly",
            ct: TestContext.Current.CancellationToken,
            jobRunId: "run-123",
            initialCols: 211,
            initialRows: 47);
        var initialPayload = "initial native frame"u8.ToArray();
        var resizedPayload = "resized native frame"u8.ToArray();

        service.LogOutput(sessionId, initialPayload);
        service.RecordResize(sessionId, 222, 55, TerminalIoSource.LocalCli);
        service.LogOutput(sessionId, resizedPayload);
        await service.CompleteSessionAsync(sessionId, 0);

        List<TerminalOutputWrite> enriched;
        lock (persisted)
        {
            Assert.All(persisted, entry => Assert.Equal(sessionId, entry.SessionId));
            enriched = persisted.Select(entry => entry.Row)
                .Where(row => row.Kind == TerminalOutputKind.Enriched)
                .ToList();
        }

        Assert.Single(enriched, row => row.Data.SequenceEqual(initialPayload) && !row.IsAlternateScreen && row.Cols == 211 && row.Rows == 47);
        Assert.Single(enriched, row => row.Data.Length == 0 && !row.IsAlternateScreen && row.Cols == 222 && row.Rows == 55);
        Assert.Single(enriched, row => row.Data.SequenceEqual(resizedPayload) && !row.IsAlternateScreen && row.Cols == 222 && row.Rows == 55);
    }
}
