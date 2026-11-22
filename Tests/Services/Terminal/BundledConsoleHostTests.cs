using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Moq;
using Pty.Net;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;
using ConPtyApi = Pty.Net.Windows.ConPtyApi;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-354: the bundled Windows console host (conpty.dll + OpenConsole.exe) behind a setting.
/// The host-backed cases start the real pair from this build's output and a throwaway child; no
/// VibeRails host, database, CLI or network request is involved.
/// </summary>
public sealed class BundledConsoleHostTests
{
    public static bool IsWindowsX64 =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private const string NeedsWindows = "Requires Windows x64 and the bundled console host";

    // ---- The setting ---------------------------------------------------------------------

    [Fact]
    public void NewAndOlderSettingsLeaveTheBuiltInHostOn()
    {
        Assert.False(new Settings().ModernConsoleHost);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings)!.ModernConsoleHost);
    }

    [Theory]
    [InlineData(false, false, true, false)]  // Off: the built-in host, whatever else is true.
    [InlineData(true, false, true, true)]    // On, a tab, Windows x64: the bundled host.
    [InlineData(true, true, true, false)]    // A native console session would answer queries twice.
    [InlineData(true, false, false, false)]  // Not Windows x64.
    public void OnlyATabTerminalOnWindowsWithTheSettingOnAsksForTheBundledHost(
        bool settingOn, bool nativeConsole, bool supportedPlatform, bool expectBundled)
    {
        var path = BundledConsoleHost.ResolveForNewTerminal(
            new Settings { ModernConsoleHost = settingOn }, nativeConsole, supportedPlatform, @"C:\app\conpty\conpty.dll");
        Assert.Equal(expectBundled ? @"C:\app\conpty\conpty.dll" : null, path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheRunnerReadsTheSettingWhenATerminalStartsAndKeepsNativeSessionsOnTheBuiltInHost(
        bool settingOn, bool nativeConsole)
    {
        var reads = 0;
        var runner = Runner(() => { reads++; return new Settings { ModernConsoleHost = settingOn }; });

        var path = runner.ResolveBundledConsoleHost(nativeConsole);

        var expectBundled = settingOn && !nativeConsole && IsWindowsX64;
        Assert.Equal(expectBundled ? BundledConsoleHost.LibraryPath : null, path);
        // Read per start, never cached: the choice follows the shared settings file.
        runner.ResolveBundledConsoleHost(nativeConsole);
        Assert.Equal(IsWindowsX64 ? 2 : 0, reads);
    }

    [Fact]
    public void AnUnreadableSettingsFileMeansTheBuiltInHostNotAFailedLaunch()
    {
        var runner = Runner(() => throw new IOException("settings.json is locked"));
        Assert.Null(runner.ResolveBundledConsoleHost(isNativeCli: false));
    }

    [Fact]
    public void BothSpawnPathsOfTheRunnerPassTheChoiceOn()
    {
        // A direct-spawn launch (Board cards, saved launches) and an interactive shell tab are two
        // calls. Dropping the argument from one leaves that kind of terminal on the built-in host
        // with the setting on, and nothing else would notice.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "VibeRails", "Services", "Terminal", "Core", "TerminalRunner.cs"));
        Assert.Equal(2, CountOf(source, "terminal = await Terminal.CreateAsync("));
        Assert.Equal(2, CountOf(source, "bundledConsoleHostPath: bundledConsoleHostPath);"));
    }

    private static TerminalRunner Runner(Func<Settings> loadSettings) => new(
        Mock.Of<ITerminalStateService>(), Mock.Of<ICommandService>(), Mock.Of<ILocalToolApiContext>(),
        Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(), Mock.Of<IRepository>(),
        Mock.Of<IEnvironmentStepRunner>(), Mock.Of<IAppEventBus>(), loadSettings: loadSettings);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("VibeRails.slnx was not found above the test output directory.");
    }

    [Fact]
    public void EveryWindowsPtyLogsTheHostItGotAndAFallbackIsAWarningThatSaysWhy()
    {
        const string path = @"C:\app\conpty\conpty.dll";
        const string reason = "OpenConsole.exe is missing beside conpty.dll";

        var bundled = TerminalPty.DescribeConsoleHost(PtyConsoleHost.Bundled, 7, path, null)!.Value;
        Assert.Equal(Serilog.Events.LogEventLevel.Information, bundled.Level);
        Assert.Equal("[Terminal] PTY {Pid} is hosted by the bundled console host {Path}", bundled.Template);
        Assert.Equal([7, path], bundled.Values);

        // The terminal works after a fallback, so this warning is the only sign of one.
        var fallback = TerminalPty.DescribeConsoleHost(PtyConsoleHost.Inbox, 7, path, reason)!.Value;
        Assert.Equal(Serilog.Events.LogEventLevel.Warning, fallback.Level);
        Assert.Contains("built-in console host although the modern host is turned on: {Reason}", fallback.Template);
        Assert.Equal([7, reason], fallback.Values);

        var settingOff = TerminalPty.DescribeConsoleHost(PtyConsoleHost.Inbox, 7, null, null)!.Value;
        Assert.Equal(Serilog.Events.LogEventLevel.Information, settingOff.Level);
        Assert.Equal("[Terminal] PTY {Pid} is hosted by the built-in console host", settingOff.Template);
        Assert.Equal([7], settingOff.Values);

        // macOS and Linux have no console host to name.
        Assert.Null(TerminalPty.DescribeConsoleHost(PtyConsoleHost.None, 7, null, null));
        var unsupported = TerminalPty.DescribeConsoleHost(PtyConsoleHost.None, 7, path, null)!.Value;
        Assert.Equal(Serilog.Events.LogEventLevel.Warning, unsupported.Level);
        Assert.Equal([7, "this platform has no bundled console host"], unsupported.Values);
    }

    [Fact]
    public void TheHostIsAlwaysLookedForBesideTheExecutableNeverSearchedFor()
    {
        Assert.True(Path.IsPathFullyQualified(BundledConsoleHost.LibraryPath));
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "conpty", "conpty.dll"),
            BundledConsoleHost.LibraryPath);
    }

    [Fact]
    public void AvailabilityNeedsBothFilesAndASupportedPlatform()
    {
        using var directory = new TempDirectory();
        var library = Path.Combine(directory.Path, "conpty.dll");

        Assert.False(BundledConsoleHost.IsAvailable(library, supportedPlatform: true, out var reason));
        Assert.Contains("does not include", reason);

        File.WriteAllText(library, "x");
        Assert.False(BundledConsoleHost.IsAvailable(library, supportedPlatform: true, out _));

        File.WriteAllText(Path.Combine(directory.Path, "OpenConsole.exe"), "x");
        Assert.True(BundledConsoleHost.IsAvailable(library, supportedPlatform: true, out reason));
        Assert.Null(reason);

        Assert.False(BundledConsoleHost.IsAvailable(library, supportedPlatform: false, out reason));
        Assert.Contains("Windows x64", reason);
    }

    // ---- The loader ----------------------------------------------------------------------

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public void ThisBuildCarriesBothFilesSideBySide()
    {
        // The package's own targets would put OpenConsole.exe in an x64\ subfolder, and only for
        // a build whose PlatformTarget names an architecture. VibeRails.csproj copies the pair
        // explicitly; this fails if that copy is lost.
        Assert.True(BundledConsoleHost.IsAvailable(out var reason), reason);
    }

    [Theory(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    [InlineData("conpty.dll")]
    [InlineData(@"conpty\conpty.dll")]
    [InlineData(@".\conpty.dll")]
    [InlineData("")]
    public void APathThatIsNotAbsoluteIsRefusedBeforeAnythingIsLoaded(string path)
    {
        // A bare or relative name would be resolved against the working directory, which is the
        // repository the terminal was opened for (VIBE-310).
        Assert.Null(ConPtyApi.TryLoadBundled(path, out var failure));
        Assert.Contains("not an absolute path", failure);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public void AMissingLibraryIsReportedNotThrown()
    {
        using var directory = new TempDirectory();
        Assert.Null(ConPtyApi.TryLoadBundled(Path.Combine(directory.Path, "conpty.dll"), out var failure));
        Assert.Contains("was not found", failure);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public void ALibraryWithoutOpenConsoleBesideItIsRefused()
    {
        // Measured: such a conpty.dll starts conhost.exe and still returns success. Loading it
        // would report the pass-through host while running the built-in one.
        using var directory = new TempDirectory();
        var library = Path.Combine(directory.Path, "conpty.dll");
        File.Copy(BundledConsoleHost.LibraryPath, library);

        Assert.Null(ConPtyApi.TryLoadBundled(library, out var failure));
        Assert.Contains("OpenConsole.exe is missing", failure);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public void AFileThatIsNotALibraryIsReportedNotThrown()
    {
        using var directory = new TempDirectory();
        var library = Path.Combine(directory.Path, "conpty.dll");
        File.WriteAllText(library, "not a library");
        File.WriteAllText(Path.Combine(directory.Path, "OpenConsole.exe"), "not a program");

        Assert.Null(ConPtyApi.TryLoadBundled(library, out var failure));
        Assert.Contains("could not be loaded", failure);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public void TheShippedPairLoads()
    {
        var api = ConPtyApi.TryLoadBundled(BundledConsoleHost.LibraryPath, out var failure);
        Assert.True(api is not null, failure);
        Assert.Equal(PtyConsoleHost.Bundled, api!.Host);
        Assert.Same(api, ConPtyApi.TryLoadBundled(BundledConsoleHost.LibraryPath, out _));
    }

    // ---- A real terminal on each host ------------------------------------------------------

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task WithoutTheOptionATerminalUsesTheBuiltInHostAndAnswersNothing()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        await using var terminal = await EchoTerminalAsync(bundledPath: null, ct);
        var output = await RunToExitAsync(terminal, ct);

        Assert.Equal(PtyConsoleHost.Inbox, terminal.ConsoleHost);
        Assert.Contains("PROBE_HELLO", output);
        Assert.Equal(0, terminal.AnsweredTerminalQueries);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task TheBundledHostPassesOutputThroughAndItsStartupQueryIsAnswered()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        await using var terminal = await EchoTerminalAsync(BundledConsoleHost.LibraryPath, ct);
        var output = await RunToExitAsync(terminal, ct);

        Assert.Equal(PtyConsoleHost.Bundled, terminal.ConsoleHost);
        Assert.Contains("PROBE_HELLO\r\n", output);
        // The built-in host re-renders: it clears the screen and homes the cursor before the
        // program's first byte. The bundled host sends what the program wrote.
        Assert.DoesNotContain("\u001b[2J", output);
        // Its device-attributes query at startup is forwarded to us, and we answer it. Left
        // unanswered, the program's own first query would have its answer swallowed instead.
        Assert.Contains("\u001b[c", output);
        Assert.True(terminal.AnsweredTerminalQueries >= 1, "the host's startup query was not answered");
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task AnUnusableBundledHostFallsBackToTheBuiltInOneAndTheTerminalStillWorks()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        using var directory = new TempDirectory();
        var alone = Path.Combine(directory.Path, "conpty.dll");
        File.Copy(BundledConsoleHost.LibraryPath, alone);

        await using var terminal = await EchoTerminalAsync(alone, ct);
        var output = await RunToExitAsync(terminal, ct);

        Assert.Equal(PtyConsoleHost.Inbox, terminal.ConsoleHost);
        Assert.Contains("PROBE_HELLO", output);
        Assert.Equal(0, terminal.AnsweredTerminalQueries);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task TheFallbackReasonReachesTheCaller()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        using var directory = new TempDirectory();
        using var pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = CmdPath, CommandLine = ["/d", "/c", "exit"], Cwd = Path.GetTempPath(), Cols = 80, Rows = 24,
            WindowsConPtyLibraryPath = Path.Combine(directory.Path, "conpty.dll")
        }, ct);

        Assert.Equal(PtyConsoleHost.Inbox, pty.ConsoleHost);
        Assert.Contains("was not found", pty.ConsoleHostFallbackReason);
        pty.Kill();
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task APackagedHostThatHoldsStartupForItsQueryWouldBeNoticed()
    {
        // Nothing here answers the host. The 1.25.2611 build starts the program at once; the
        // older 1.25.2603 build VS Code 1.147 ships held every start for 3 seconds. The responder
        // runs from the read loop, which a caller starts after wiring its consumers, so a package
        // update that brings the hold back must change that order, and this test says so first.
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        var clock = Stopwatch.StartNew();
        using var pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = CmdPath, CommandLine = ["/d", "/c", "echo PROBE_HELLO"], Cwd = Path.GetTempPath(), Cols = 80, Rows = 24,
            WindowsConPtyLibraryPath = BundledConsoleHost.LibraryPath
        }, ct);
        Assert.Equal(PtyConsoleHost.Bundled, pty.ConsoleHost);

        var seen = new StringBuilder();
        var buffer = new byte[4096];
        while (!seen.ToString().Contains("PROBE_HELLO"))
        {
            var read = await pty.ReaderStream.ReadAsync(buffer, ct);
            Assert.True(read > 0, "the pipe closed before the program's output arrived");
            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        Assert.True(clock.ElapsedMilliseconds < 1500,
            $"the program's first output took {clock.ElapsedMilliseconds} ms with the startup query unanswered");
        pty.Kill();
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task AProgramsOwnQueriesAreAnsweredOnceEachWithTheEmulatorsCursor()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        await using var probe = await ChildProbe.StartAsync("vt", BundledConsoleHost.LibraryPath, ct);

        // The program moves to row 5, column 9 and asks where it is. Under the built-in host
        // conhost answers from its own buffer; here the question reaches us.
        Assert.Equal("CPR," + Hex("\u001b[5;9R"), await probe.ReadLineAsync("CPR,", ct));
        // The host swallowed the first device-attributes answer it ever got (its own startup
        // query). This one is the program's, and must arrive.
        Assert.Equal("DA1," + Hex("\u001b[?1;2c"), await probe.ReadLineAsync("DA1,", ct));
        Assert.Equal("DA2," + Hex("\u001b[>0;276;0c"), await probe.ReadLineAsync("DA2,", ct));
        Assert.Equal("DSR," + Hex("\u001b[0n"), await probe.ReadLineAsync("DSR,", ct));
        Assert.Equal("XCPR," + Hex("\u001b[?5;9R"), await probe.ReadLineAsync("XCPR,", ct));
        // Synchronized output is supported by the renderer and not switched on yet. The built-in
        // host answers this one "not recognized", so programs never bracket their frames under it.
        Assert.Equal("MODE," + Hex("\u001b[?2026;2$y"), await probe.ReadLineAsync("MODE,", ct));
        // Nothing answered twice, and nothing arrived that was not asked for.
        Assert.Equal("EXTRA,", await probe.ReadLineAsync("EXTRA,", ct));
        // The host's own startup query, then the program's six.
        Assert.Equal(7, probe.Terminal.AnsweredTerminalQueries);
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task AfterAResizeTheHostIsToldTheCursorAtOnceAndNoKeyIsInvented()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        await using var probe = await ChildProbe.StartAsync("keys", BundledConsoleHost.LibraryPath, ct);
        var before = probe.Terminal.AnsweredTerminalQueries;

        probe.Terminal.Resize(80, 24);

        // After a resize the host asks for the cursor before it serves the next console call that
        // needs it, and that call waits for the answer: 500 ms when nobody gives one (measured,
        // 1.25.2611). An idle program is not asked about until it next touches the console.
        var cursorRead = await probe.ReadLineAsync("B,", ct);
        var took = int.Parse(cursorRead[(cursorRead.IndexOf("took=", StringComparison.Ordinal) + 5)..]);
        Assert.True(took < 300, $"the program's cursor read took {took} ms after a resize: {cursorRead}");
        Assert.True(probe.Terminal.AnsweredTerminalQueries > before, "the resize query was not answered");

        // A second answer would be an F3 keypress (virtual key 114); a stray device-attributes
        // answer would be seven typed characters.
        await Task.Delay(700, ct);
        Assert.DoesNotContain(probe.Lines, line => line.StartsWith("D,", StringComparison.Ordinal));
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task AShorterTerminalKeepsItsLastLinesSoAReconnectShowsWhatTheViewerHas()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        // Forty lines into thirty rows, then a program that waits: the cursor is on the last row.
        await using var terminal = await TerminalPty.CreateAsync(
            Path.GetTempPath(), new Dictionary<string, string>(), cols: 100, rows: 30, ct: ct,
            app: CmdPath, argv: ["/d", "/c", "(for /L %i in (1,1,40) do @echo row %i) & pause >nul"],
            bundledConsoleHostPath: BundledConsoleHost.LibraryPath);
        Assert.Equal(PtyConsoleHost.Bundled, terminal.ConsoleHost);
        terminal.StartReadLoop();
        await WaitForScreenAsync(terminal, screen => screen[28] == "row 40" && screen[0] == "row 12", ct);

        terminal.Resize(100, 12);

        // Nothing repaints after this resize, and this program is idle, so the host does not even
        // ask where the cursor is yet. The viewer keeps the bottom of the screen (xterm.js), so
        // the emulator must as well, or the next snapshot shows rows 12 to 23 where the viewer
        // shows 30 to 40.
        string[] expected = [.. Enumerable.Range(30, 11).Select(row => $"row {row}"), ""];
        Assert.Equal(expected, terminal.GetScreenText());
        await Task.Delay(400, ct);
        Assert.Equal(expected, terminal.GetScreenText());
    }

    private static async Task WaitForScreenAsync(TerminalPty terminal, Func<string[], bool> ready, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var screen = terminal.GetScreenText();
            if (ready(screen)) return;
            Assert.True(DateTime.UtcNow < deadline,
                "timed out waiting for the terminal; its screen is:\n" + string.Join('\n', screen.Where(line => line.Length > 0)));
            await Task.Delay(25, ct);
        }
    }

    [Fact(SkipUnless = nameof(IsWindowsX64), Skip = NeedsWindows)]
    public async Task TypedKeysStillArriveAsTheSameRecordsUnderTheBundledHost()
    {
        var ct = Deadline(out var timeout);
        using var _ = timeout;
        await using var probe = await ChildProbe.StartAsync("keys", BundledConsoleHost.LibraryPath, ct);

        await probe.Terminal.WriteInputBytesAsync("x"u8.ToArray(), ct);
        Assert.Equal("D,88,120,0", await probe.ReadLineAsync("D,", ct));
        await probe.Terminal.WriteInputBytesAsync("\u001b[Z"u8.ToArray(), ct);
        Assert.Equal("D,9,9,16", await probe.ReadLineAsync("D,9,", ct));
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string Hex(string text) => Convert.ToHexString(Encoding.ASCII.GetBytes(text));

    private static CancellationToken Deadline(out CancellationTokenSource source)
    {
        source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(40));
        return source.Token;
    }

    private static Task<TerminalPty> EchoTerminalAsync(string? bundledPath, CancellationToken ct) =>
        TerminalPty.CreateAsync(
            Path.GetTempPath(), new Dictionary<string, string>(), cols: 100, rows: 30, ct: ct,
            app: CmdPath, argv: ["/d", "/c", "echo PROBE_HELLO"], bundledConsoleHostPath: bundledPath);

    private static async Task<string> RunToExitAsync(TerminalPty terminal, CancellationToken ct)
    {
        var output = new CapturingConsumer();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, _) => exited.TrySetResult();
        terminal.Subscribe(output);
        terminal.StartReadLoop();
        if (terminal.HasExited) exited.TrySetResult();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        // The answer to the startup query is written by the responder's pump, not the read loop.
        await Task.Delay(100, ct);
        return output.Text;
    }

    private sealed class CapturingConsumer : ITerminalConsumer
    {
        private readonly MemoryStream _bytes = new();
        public void OnOutput(ReadOnlyMemory<byte> data) { lock (_bytes) _bytes.Write(data.Span); }
        public string Text { get { lock (_bytes) return Encoding.UTF8.GetString(_bytes.ToArray()); } }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "viberails-conhost-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A console client inside the pseudoconsole that reports what it sees over a named pipe, so
    /// nothing is read back through a terminal renderer. Windows PowerShell hosts it because it
    /// carries an installed compiler.
    /// </summary>
    private sealed class ChildProbe : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly List<string> _lines = [];
        private readonly SemaphoreSlim _arrived = new(0);
        private int _next;

        public TerminalPty Terminal { get; }

        public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }

        private ChildProbe(TerminalPty terminal, NamedPipeServerStream pipe)
        {
            Terminal = terminal;
            _pipe = pipe;
        }

        public static async Task<ChildProbe> StartAsync(string mode, string bundledPath, CancellationToken ct)
        {
            var pipeName = "vibe_conhost_test_" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var script = "Add-Type -TypeDefinition @'\n" + ChildSource + "\n'@\n[ConsoleHostProbe]::Run('" + pipeName + "', '" + mode + "')";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var terminal = await TerminalPty.CreateAsync(
                Path.GetTempPath(), new Dictionary<string, string>(), cols: 100, rows: 30, ct: ct,
                app: powershell,
                argv: ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                bundledConsoleHostPath: bundledPath);
            var probe = new ChildProbe(terminal, pipe);
            try
            {
                Assert.Equal(PtyConsoleHost.Bundled, terminal.ConsoleHost);
                terminal.StartReadLoop(); // Also drains output so the child cannot block on its pipe.
                await pipe.WaitForConnectionAsync(ct);
                _ = probe.PumpAsync(ct);
                await probe.ReadLineAsync("READY", ct);
                return probe;
            }
            catch
            {
                await probe.DisposeAsync();
                throw;
            }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(_pipe, leaveOpen: true);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    lock (_lines) _lines.Add(line);
                    _arrived.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>The next unread line that starts with <paramref name="prefix"/>; earlier lines are skipped.</summary>
        public async Task<string> ReadLineAsync(string prefix, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                lock (_lines)
                {
                    while (_next < _lines.Count)
                    {
                        var line = _lines[_next++];
                        if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
                    }
                }

                try { await _arrived.WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Assert.Fail($"The console client never reported a line starting with '{prefix}'. Seen: {string.Join(" | ", Lines)}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Terminal.DisposeAsync();
            await _pipe.DisposeAsync();
        }

        // C# 5: this is compiled by the .NET Framework compiler behind Windows PowerShell.
        private const string ChildSource = """
            using System;
            using System.Collections.Generic;
            using System.Diagnostics;
            using System.IO;
            using System.IO.Pipes;
            using System.Runtime.InteropServices;
            using System.Text;
            using System.Threading;
            public static class ConsoleHostProbe
            {
                [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
                [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
                [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
                [DllImport("kernel32.dll")] private static extern bool ReadConsoleInputW(IntPtr handle, out InputRecord record, uint length, out uint count);
                [DllImport("kernel32.dll")] private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint length, out uint read, IntPtr overlapped);
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool WriteConsoleW(IntPtr handle, string text, uint length, out uint written, IntPtr reserved);
                [DllImport("kernel32.dll")] private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ScreenInfo info);
                [StructLayout(LayoutKind.Sequential)]
                private struct ScreenInfo { public short SizeX, SizeY, CursorX, CursorY; public ushort Attributes; public short Left, Top, Right, Bottom, MaxX, MaxY; }
                [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
                private struct InputRecord
                {
                    [FieldOffset(0)] public ushort EventType;
                    [FieldOffset(4)] public int KeyDown;
                    [FieldOffset(4)] public short SizeX;
                    [FieldOffset(6)] public short SizeY;
                    [FieldOffset(10)] public ushort VirtualKey;
                    [FieldOffset(14)] public char Character;
                    [FieldOffset(16)] public uint ControlKeyState;
                }

                private static StreamWriter writer;
                private static IntPtr input;
                private static IntPtr output;
                private static readonly object gate = new object();
                private static readonly List<byte> received = new List<byte>();

                public static void Run(string pipeName, string mode)
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                    {
                        pipe.Connect(10000);
                        writer = new StreamWriter(pipe, new UTF8Encoding(false));
                        writer.AutoFlush = true;
                        input = GetStdHandle(-10);
                        output = GetStdHandle(-11);
                        uint inputMode;
                        uint outputMode;
                        if (!GetConsoleMode(input, out inputMode) || !GetConsoleMode(output, out outputMode))
                            throw new InvalidOperationException("Not attached to a console");
                        SetConsoleMode(output, outputMode | 4u | 1u);
                        if (mode == "vt") RunVt(); else RunKeys(inputMode);
                    }
                }

                // A program that speaks VT on both sides: it writes queries and reads the answers as bytes.
                private static void RunVt()
                {
                    if (!SetConsoleMode(input, 0x200u))
                        throw new InvalidOperationException("Unable to enter VT input mode");
                    var reader = new Thread(delegate()
                    {
                        var buffer = new byte[256];
                        uint read;
                        while (ReadFile(input, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
                            lock (gate) for (int i = 0; i < read; i++) received.Add(buffer[i]);
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    writer.WriteLine("READY");
                    Ask("CPR", "\u001b[5;9H\u001b[6n");
                    Ask("DA1", "\u001b[c");
                    Ask("DA2", "\u001b[>c");
                    Ask("DSR", "\u001b[5n");
                    Ask("XCPR", "\u001b[?6n");
                    Ask("MODE", "\u001b[?2026$p");
                    Thread.Sleep(700);
                    Report("EXTRA");
                }

                private static void Ask(string name, string query)
                {
                    uint written;
                    WriteConsoleW(output, query, (uint)query.Length, out written, IntPtr.Zero);
                    Thread.Sleep(600);
                    Report(name);
                }

                private static void Report(string name)
                {
                    lock (gate)
                    {
                        writer.WriteLine(name + "," + BitConverter.ToString(received.ToArray()).Replace("-", ""));
                        received.Clear();
                    }
                }

                // A program that reads legacy key records, as crossterm and libuv do, and times
                // one cursor read after each resize.
                private static void RunKeys(uint inputMode)
                {
                    if (!SetConsoleMode(input, (inputMode & ~(2u | 4u | 512u)) | 8u))
                        throw new InvalidOperationException("Unable to enter legacy console input mode");
                    writer.WriteLine("READY");
                    while (true)
                    {
                        InputRecord record;
                        uint count;
                        if (!ReadConsoleInputW(input, out record, 1, out count))
                            throw new InvalidOperationException("ReadConsoleInputW failed");
                        if (count == 0) continue;
                        if (record.EventType == 4)
                        {
                            var clock = Stopwatch.StartNew();
                            ScreenInfo info;
                            GetConsoleScreenBufferInfo(output, out info);
                            writer.WriteLine("B,size=" + record.SizeX + "x" + record.SizeY + ",took=" + clock.ElapsedMilliseconds);
                            continue;
                        }
                        if (record.EventType != 1) continue;
                        if (record.VirtualKey == 16 || record.VirtualKey == 17 || record.VirtualKey == 18) continue;
                        writer.WriteLine((record.KeyDown == 0 ? "U" : "D") + "," + record.VirtualKey + "," + (int)record.Character + "," + record.ControlKeyState);
                    }
                }
            }
            """;
    }
}
