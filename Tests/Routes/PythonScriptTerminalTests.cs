using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PyBridge;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.PythonScripts;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Routes;

/// <summary>Real WebSocket + PTY regressions. Uses only disposable registered script fixtures.</summary>
public sealed class PythonScriptTerminalTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-script-terminal", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(40));
    private WebApplication _app = null!;
    private PythonScriptService _scripts = null!;
    private CancellationToken Token => _timeout.Token;

    public async ValueTask InitializeAsync()
    {
        var executable = Environment.GetEnvironmentVariable("VIBERAILS_TEST_PYTHON");
        var options = string.IsNullOrEmpty(executable) ? PythonRunnerOptions.Discover() : new PythonRunnerOptions { PythonExecutable = executable };
        _scripts = new PythonScriptService(new PythonRunner(options), installDirectory: _directory);
        await _scripts.SetPinAsync(new SetPythonScriptPinRequest(null, "1234"), Token);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IPythonScriptService>(_scripts);
        builder.Services.AddSingleton(Mock.Of<ITerminalTabHostService>());
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        _app = builder.Build();
        _app.UseWebSockets();
        PythonScriptRoutes.Map(_app, true);
        await _app.StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        _timeout.Dispose();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task UsersExactInputExampleWaitsForTypingThenPrintsAndExits()
    {
        await SignAsync("print(\"Hey!\")\ns = input(\"Enter something\")\nprint(s)\n");
        using var socket = await ConnectAsync();
        var before = await ReadUntilAsync(socket, text => text.Contains("Enter something"));
        Assert.Contains("Hey!", before);
        Assert.DoesNotContain("exit:", before);
        await InputAsync(socket, "typed-live-54321\r");
        var after = await ReadUntilAsync(socket, text => text.Contains("exit:0"));
        Assert.Contains("typed-live-54321", after);
        Assert.DoesNotContain("EOFError", after);
        Assert.Empty(_scripts.GetRunHistory().Runs);
        Assert.DoesNotContain(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("typed-live-54321"));
    }

    [Fact]
    public async Task PasswordPromptDoesNotEchoOrPersistThePassword()
    {
        await SignAsync("import getpass\ns = getpass.getpass('Password: ')\nprint('Accepted ' + str(len(s)))\n");
        using var socket = await ConnectAsync();
        await ReadUntilAsync(socket, text => text.Contains("Password:"));
        await InputAsync(socket, "private-input-9362\r");
        var output = await ReadUntilAsync(socket, text => text.Contains("exit:0"));
        Assert.Contains("Accepted 18", output);
        Assert.DoesNotContain("private-input-9362", output);
        Assert.DoesNotContain(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("private-input-9362"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseAndStopTerminateThePrivateProcess(bool stop)
    {
        await SignAsync("import os\nprint('PID=' + str(os.getpid()), flush=True)\ninput('Waiting')\n");
        using var socket = await ConnectAsync();
        var output = await ReadUntilAsync(socket, text => text.Contains("Waiting"));
        var pid = int.Parse(System.Text.RegularExpressions.Regex.Match(output, @"PID=(\d+)").Groups[1].Value);
        using var process = Process.GetProcessById(pid);
        if (stop)
        {
            await socket.SendAsync("stop"u8.ToArray(), WebSocketMessageType.Text, true, Token);
            await ReadUntilAsync(socket, text => text.Contains("stopped"));
        }
        else
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Window closed", Token);
        }
        await process.WaitForExitAsync(Token);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task RunRechecksSignatureAndRequiredPinBeforeStarting()
    {
        await SignAsync("print('SHOULD_NOT_RUN')\n");
        await _scripts.SetRunPinRequirementAsync(new PythonScriptRunPinRequirementRequest("test.py", true, "1234"), Token);
        using (var denied = await ConnectAsync(pin: "wrong"))
        {
            var text = await ReadUntilAsync(denied, text => text.Contains("error:"));
            Assert.Contains("signing PIN", text);
            Assert.DoesNotContain("ready", text);
        }
        File.WriteAllText(Path.Combine(_directory, PythonScriptService.ScriptsSubdirectory, "test.py"), "print('changed')\n");
        using var changed = await ConnectAsync(pin: "1234");
        var rejected = await ReadUntilAsync(changed, text => text.Contains("error:"));
        Assert.DoesNotContain("ready", rejected);
    }

    private async Task SignAsync(string content)
    {
        await _scripts.CreateAsync(new PythonScriptSaveRequest("test.py", content), Token);
        await _scripts.ApproveAsync(new PythonScriptApprovalRequest("test.py", "1234"), Token);
    }

    private async Task<ClientWebSocket> ConnectAsync(string? pin = null)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_app.Urls.Single().Replace("http:", "ws:") + "/api/v1/python-scripts/run/terminal/ws"), Token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PythonScriptTerminalRequest("test.py", pin),
            AppJsonSerializerContext.Default.PythonScriptTerminalRequest);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, Token);
        return socket;
    }

    private Task InputAsync(ClientWebSocket socket, string input) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(input), WebSocketMessageType.Binary, true, Token);

    private static string PlainText(string value) => System.Text.RegularExpressions.Regex.Replace(value,
        @"\x1B(?:\][^\x07]*(?:\x07)|\[[0-?]*[ -/]*[@-~])", "");

    private async Task<string> ReadUntilAsync(ClientWebSocket socket, Func<string, bool> done)
    {
        var text = new StringBuilder();
        var bytes = new byte[16384];
        while (!done(PlainText(text.ToString())))
        {
            WebSocketReceiveResult received;
            try { received = await socket.ReceiveAsync(bytes, Token); }
            catch (OperationCanceledException) { Assert.Fail($"Timed out waiting for terminal. Received: {text}"); throw; }
            Assert.NotEqual(WebSocketMessageType.Close, received.MessageType);
            text.Append(Encoding.UTF8.GetString(bytes, 0, received.Count));
            // xterm answers the console's cursor-position query before getpass reads keys.
            if (Encoding.UTF8.GetString(bytes, 0, received.Count).Contains("\u001b[6n"))
                await InputAsync(socket, "\u001b[1;1R");
        }
        return PlainText(text.ToString());
    }
}
