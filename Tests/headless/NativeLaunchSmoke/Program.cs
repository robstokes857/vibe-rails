using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Launches;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;
using VibeRails.Services.Cli;
using VibeRails.Services.Environments;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

// Opt-in, interactive smoke test. Opens real native windows, but runs only fixed local
// PowerShell fixtures. The app's configuration, databases and installed CLIs are not used.
var parsed = ArgumentParser.Parse(args);
if (parsed.LaunchRunId is { } id)
{
    var directory = parsed.WorkDir!;
    await using var services = BuildServices(directory);
    File.WriteAllText(Path.Combine(directory, "console"), (!Console.IsOutputRedirected).ToString());
    return await NativeLaunchRunner.RunAsync(id, services);
}
if (!args.SequenceEqual(new[] { "--native-smoke" }) || !OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("On Windows, run with --native-smoke to open four temporary test terminals.");
    return 2;
}

var root = Path.Combine(Path.GetTempPath(), "viberails-native-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
try
{
    foreach (var script in new[] { true, false })
    foreach (var cancel in new[] { false, true })
    {
        var directory = Path.Combine(root, $"{(script ? "script" : "preset")}-{cancel}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "command.ps1"), """
            Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ready') -Value $PID
            Write-Output 'NATIVE_LAUNCH_FIXTURE'
            """ + $"\nStart-Sleep -Seconds {(cancel ? 60 : 3)}\nexit 0\n");
        await using var services = BuildServices(directory);
        var store = services.GetRequiredService<ILaunchStore>();
        var service = services.GetRequiredService<LaunchService>();
        var definition = script
            ? (await service.SaveActionAsync(directory, null, new("Smoke script", "", "script",
                new("command.ps1", JobScriptRuntime.PowerShell, [])), timeout.Token)).Id
            : (await service.SavePresetAsync(null, new("Smoke preset", "", "codex", InitialPrompt: "Fixture"), timeout.Token)).Preset.Id;
        var run = await service.EnqueueAsync(script ? "action" : "preset", definition,
            new(directory, directory, "manual", Guid.NewGuid().ToString("N"),
                ContextIncluded: true, ComposedPrompt: "Fixture", NativeTerminal: true), timeout.Token);
        var dispatcher = new LaunchDispatcher(store, service, Mock.Of<ITerminalTabHostService>(MockBehavior.Strict),
            services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IBoardStore>(), new JobProcessLauncher());
        await dispatcher.DispatchAsync(run.Id, timeout.Token);
        try
        {
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                run = (await store.GetRunAsync(run.Id, timeout.Token))!;
                if (run.IsTerminal) throw new Exception("Launch failed before the fixture ran: " + run.Error);
                await Task.Delay(100, timeout.Token);
            }
            var childPid = int.Parse(File.ReadAllText(Path.Combine(directory, "ready")).Trim());
            if (cancel) await store.CancelAsync(run.Id, run.ProjectPath, timeout.Token);
            do
            {
                await Task.Delay(100, timeout.Token);
                run = (await store.GetRunAsync(run.Id, timeout.Token))!;
            } while (!run.IsTerminal);
            if (run.Status != (cancel ? "cancelled" : "succeeded") || run.TabId is not null || run.SessionId is null)
                throw new Exception($"Unexpected native run outcome: {run.Status}: {run.Error}");
            if (File.ReadAllText(Path.Combine(directory, "console")) != "True")
                throw new Exception("Native child did not inherit a console.");
            if (!File.ReadAllText(Path.Combine(directory, "output")).Contains("NATIVE_LAUNCH_FIXTURE"))
                throw new Exception("Native output did not reach the recording boundary.");
            if (IsAlive(childPid)) throw new Exception("Fixture process survived run completion.");
            Console.WriteLine($"PASS {(script ? "script" : "preset")}: native console, execution, output, {run.Status}, child exited");
            // Wait for the native wrapper to release the fixture database before cleanup.
            try
            {
                using var owner = Process.GetProcessById(run.OwnerPid!.Value);
                await owner.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { /* The native child already exited. */ }
        }
        catch
        {
            await store.CancelAsync(run.Id, run.ProjectPath, CancellationToken.None);
            throw;
        }
    }
    Directory.Delete(root, true);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.Error.WriteLine("Fixture evidence retained at " + root);
    return 1;
}

static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static ServiceProvider BuildServices(string directory)
{
    var store = new LaunchStore($"Data Source={Path.Combine(directory, "launches.db")};Pooling=False");
    var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
    string[] argv = ["-NoProfile", "-File", Path.Combine(directory, "command.ps1")];
    var scripts = new Mock<IAutomationScriptService>();
    scripts.Setup(x => x.NormalizeAsync(It.IsAny<string>(), It.IsAny<JobActionRequest>(), It.IsAny<CancellationToken>(), true))
        .ReturnsAsync((string _, JobActionRequest action, CancellationToken _, bool _) => action with { ApprovedHash = "fixture" });
    scripts.Setup(x => x.PrepareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobRunActionRecord>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new PreparedAutomationScript(executable, argv, directory, new Dictionary<string, string?>()));
    var repository = new Mock<IRepository>();
    repository.Setup(x => x.PersistTerminalOutputAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TerminalOutputWrite>>(), It.IsAny<CancellationToken>()))
        .Callback<string, IReadOnlyList<TerminalOutputWrite>, CancellationToken>((_, rows, _) =>
            File.AppendAllText(Path.Combine(directory, "output"), string.Concat(rows.Select(row => Encoding.UTF8.GetString(row.Data)))))
        .Returns(Task.CompletedTask);
    var state = new Mock<ITerminalStateService>();
    state.Setup(x => x.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
        .ReturnsAsync(() => Guid.NewGuid().ToString());
    state.Setup(x => x.LogOutput(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<TerminalIoSource>()))
        .Callback<string, ReadOnlyMemory<byte>, TerminalIoSource>((_, bytes, _) =>
            File.AppendAllText(Path.Combine(directory, "output"), Encoding.UTF8.GetString(bytes.Span)));
    var command = new Mock<ICommandService>(MockBehavior.Strict);
    command.Setup(x => x.PrepareLaunchSessionAsync(LLM.Codex, null, It.IsAny<string[]>(), "Fixture", "",
            It.IsAny<string>(), false, null, true))
        .ReturnsAsync(new PreparedTerminalSession("", "fixture", [], new(), Executable: executable, Argv: argv, InitialPrompt: "Fixture"));
    var prompt = new Mock<IPromptPlaceholderService>();
    prompt.Setup(x => x.ResolveLaunchAsync("Fixture", It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new ResolvedPrompt("Fixture", directory));
    var contact = new Mock<ILocalToolApiContext>();
    contact.Setup(x => x.BuildEnvironment(It.IsAny<string>())).Returns(new Dictionary<string, string>());
    var runner = new TerminalRunner(state.Object, command.Object, contact.Object, Mock.Of<ILlmProxySessionState>(),
        Mock.Of<IAutomationConsumer>(), repository.Object, Mock.Of<IEnvironmentStepRunner>(), Mock.Of<IAppEventBus>());
    return new ServiceCollection().AddSingleton<ILaunchStore>(store).AddSingleton(scripts.Object)
        .AddSingleton(repository.Object).AddSingleton(Mock.Of<IBoardStore>()).AddSingleton<ICliWrapper, CliWrapper>()
        .AddSingleton(new LaunchService(store, scripts.Object)).AddSingleton(prompt.Object).AddSingleton(runner)
        .AddSingleton(Mock.Of<ITerminalSessionService>()).BuildServiceProvider();
}
