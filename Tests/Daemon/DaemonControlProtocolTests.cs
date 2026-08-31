using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using VibeRails.Daemon.Ipc;
using Xunit;

namespace Tests.Daemon;

public sealed class DaemonControlProtocolTests
{
    [Fact]
    public void Protocol_HasVersionedBoundedCommandSurface()
    {
        Assert.Equal(1, DaemonControlProtocol.Version);
        Assert.Equal(4 * 1024, DaemonControlProtocol.MaximumRequestBytes);
        Assert.Equal(64 * 1024, DaemonControlProtocol.MaximumResponseBytes);

        Assert.Equal("PING", DaemonControlProtocol.ToWireName(DaemonControlCommand.Ping));
        Assert.Equal("STATUS", DaemonControlProtocol.ToWireName(DaemonControlCommand.Status));
        Assert.Equal("KICK", DaemonControlProtocol.ToWireName(DaemonControlCommand.Kick));
        Assert.Equal("SHUTDOWN", DaemonControlProtocol.ToWireName(DaemonControlCommand.Shutdown));
        Assert.True(DaemonControlProtocol.TryParseCommand("ping", out var ping));
        Assert.Equal(DaemonControlCommand.Ping, ping);
        Assert.False(DaemonControlProtocol.TryParseCommand("RUN_SHELL", out _));
    }

    [Fact]
    public async Task PipeProtocolIo_WriteLineEnforcesUtf8ByteLimit()
    {
        var testToken = TestContext.Current.CancellationToken;
        var exact = new string('\u00e9', DaemonControlProtocol.MaximumRequestBytes / 2);
        await using var accepted = new MemoryStream();

        await PipeProtocolIo.WriteLineAsync(
            accepted,
            exact,
            DaemonControlProtocol.MaximumRequestBytes,
            testToken);

        Assert.Equal(DaemonControlProtocol.MaximumRequestBytes + 1, accepted.Length);

        await using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocolIo.WriteLineAsync(
            rejected,
            exact + "x",
            DaemonControlProtocol.MaximumRequestBytes,
            testToken));
    }

    [Fact]
    public async Task PipeProtocolIo_ReadLineEnforcesByteLimit()
    {
        var testToken = TestContext.Current.CancellationToken;
        var maximum = DaemonControlProtocol.MaximumRequestBytes;
        await using var accepted = new MemoryStream(
            Encoding.UTF8.GetBytes(new string('x', maximum) + "\n"));

        var line = await PipeProtocolIo.ReadLineAsync(accepted, maximum, testToken);

        Assert.NotNull(line);
        Assert.Equal(maximum, line.Length);

        await using var rejected = new MemoryStream(
            Encoding.UTF8.GetBytes(new string('x', maximum + 1) + "\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PipeProtocolIo.ReadLineAsync(rejected, maximum, testToken));
    }

    [Fact]
    public async Task Client_RejectsOversizedRequestBeforeConnecting()
    {
        var testToken = TestContext.Current.CancellationToken;
        var client = new DaemonControlClient();
        var request = new DaemonControlRequest(
            DaemonControlProtocol.Version,
            new string('x', DaemonControlProtocol.MaximumRequestBytes));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(
            $"vbd-no-server-{Guid.NewGuid():N}",
            request,
            TimeSpan.FromMilliseconds(100),
            testToken));
    }

    [Fact]
    public async Task NamedPipe_RoundTripsCommandMessageAndPayload()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var stopping = new CancellationTokenSource();
        var payload = JsonSerializer.SerializeToElement(new { pid = 4242, ownsLease = true });
        var handler = new RecordingHandler((command, _) => ValueTask.FromResult(
            DaemonControlHandlerResult.Ok(
                command == DaemonControlCommand.Status ? "healthy" : "pong",
                command == DaemonControlCommand.Status ? payload : null)));
        var pipeName = NewPipeName();
        var serverTask = new DaemonControlServer(pipeName, handler).RunAsync(stopping.Token);

        try
        {
            var client = new DaemonControlClient();
            var ping = await client.SendAsync(
                pipeName,
                DaemonControlCommand.Ping,
                TimeSpan.FromSeconds(3),
                testToken);
            var status = await client.SendAsync(
                pipeName,
                DaemonControlCommand.Status,
                TimeSpan.FromSeconds(3),
                testToken);

            Assert.Equal(DaemonControlClientOutcome.Success, ping.Outcome);
            Assert.Equal("pong", ping.Response?.Message);
            Assert.Equal(DaemonControlClientOutcome.Success, status.Outcome);
            Assert.Equal("healthy", status.Response?.Message);
            Assert.Equal(4242, status.Response?.Payload?.GetProperty("pid").GetInt32());
            Assert.True(status.Response?.Payload?.GetProperty("ownsLease").GetBoolean());
            Assert.Equal(
                [DaemonControlCommand.Ping, DaemonControlCommand.Status],
                handler.Commands.ToArray());
        }
        finally
        {
            stopping.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3), testToken);
        }
    }

    [Fact]
    public async Task NamedPipe_ReportsProtocolMismatchAndRejectsUnknownCommandWithoutDispatch()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var stopping = new CancellationTokenSource();
        var handler = new RecordingHandler((_, _) =>
            ValueTask.FromResult(DaemonControlHandlerResult.Ok()));
        var pipeName = NewPipeName();
        var serverTask = new DaemonControlServer(pipeName, handler).RunAsync(stopping.Token);

        try
        {
            var client = new DaemonControlClient();
            var mismatch = await client.SendAsync(
                pipeName,
                new DaemonControlRequest(DaemonControlProtocol.Version + 1, "PING"),
                TimeSpan.FromSeconds(3),
                testToken);
            var unknown = await client.SendAsync(
                pipeName,
                new DaemonControlRequest(DaemonControlProtocol.Version, "RUN_SHELL"),
                TimeSpan.FromSeconds(3),
                testToken);

            Assert.Equal(DaemonControlClientOutcome.ProtocolMismatch, mismatch.Outcome);
            Assert.True(mismatch.IsReachable);
            Assert.Equal(DaemonControlProtocol.ProtocolMismatchError, mismatch.Response?.ErrorCode);
            Assert.Equal(DaemonControlClientOutcome.Rejected, unknown.Outcome);
            Assert.True(unknown.IsReachable);
            Assert.Equal(DaemonControlProtocol.UnknownCommandError, unknown.Response?.ErrorCode);
            Assert.Empty(handler.Commands);
        }
        finally
        {
            stopping.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3), testToken);
        }
    }

    [Fact]
    public async Task NamedPipe_AfterResponseCanStopServerWithoutLosingAcknowledgement()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var stopping = new CancellationTokenSource();
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler((command, _) => ValueTask.FromResult(
            DaemonControlHandlerResult.Ok(
                message: "stopping",
                afterResponse: command == DaemonControlCommand.Shutdown
                    ? () =>
                    {
                        callbackRan.TrySetResult();
                        stopping.Cancel();
                        return ValueTask.CompletedTask;
                    }
                    : null)));
        var pipeName = NewPipeName();
        var serverTask = new DaemonControlServer(pipeName, handler).RunAsync(stopping.Token);

        var result = await new DaemonControlClient().SendAsync(
            pipeName,
            DaemonControlCommand.Shutdown,
            TimeSpan.FromSeconds(3),
            testToken);

        Assert.Equal(DaemonControlClientOutcome.Success, result.Outcome);
        Assert.Equal("stopping", result.Response?.Message);
        await callbackRan.Task.WaitAsync(TimeSpan.FromSeconds(3), testToken);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3), testToken);
        Assert.True(callbackRan.Task.IsCompletedSuccessfully);
        Assert.Equal([DaemonControlCommand.Shutdown], handler.Commands.ToArray());
    }

    private static string NewPipeName() => $"vbd-tests-{Guid.NewGuid():N}";

    private sealed class RecordingHandler(
        Func<DaemonControlCommand, CancellationToken, ValueTask<DaemonControlHandlerResult>> callback)
        : IDaemonControlHandler
    {
        public ConcurrentQueue<DaemonControlCommand> Commands { get; } = new();

        public ValueTask<DaemonControlHandlerResult> HandleAsync(
            DaemonControlCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Enqueue(command);
            return callback(command, cancellationToken);
        }
    }
}
