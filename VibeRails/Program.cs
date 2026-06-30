using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Serilog;
using VibeRails;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Mcp;
using VibeRails.Services.Terminal;

using VibeRails.Utils;


// Capture launch directory FIRST (where the user ran the command from)
string launchDirectory = Directory.GetCurrentDirectory();

// Get the executable's directory (where wwwroot lives)
string exeDirectory = AppContext.BaseDirectory;
string webRootPath = Path.Combine(exeDirectory, "wwwroot");

// Configure Serilog — file sink to ~/.vibe_rails/logs/
var logDir = Path.Combine(PathConstants.GetInstallDirPath(), "logs");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logDir, "vb-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        buffered: false,
        flushToDiskInterval: TimeSpan.FromMilliseconds(250),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information(
    "[Startup] Booting processId={ProcessId} launchDirectory={LaunchDirectory} exeDirectory={ExeDirectory} argCount={ArgCount}",
    Environment.ProcessId,
    launchDirectory,
    exeDirectory,
    args.Length);

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    var typeName = eventArgs.ExceptionObject?.GetType().FullName ?? "unknown";
    ShutdownDiagnostics.RecordStopRequest(
        "AppDomain.UnhandledException",
        $"type={typeName}; terminating={eventArgs.IsTerminating}; processId={Environment.ProcessId}");

    if (eventArgs.ExceptionObject is Exception exception)
    {
        Log.Fatal(exception, "[Shutdown] Unhandled exception. terminating={IsTerminating} processId={ProcessId}", eventArgs.IsTerminating, Environment.ProcessId);
        Log.CloseAndFlush();
        return;
    }

    Log.Fatal(
        "[Shutdown] Unhandled non-Exception object. type={Type} terminating={IsTerminating} processId={ProcessId}",
        typeName,
        eventArgs.IsTerminating,
        Environment.ProcessId);
    Log.CloseAndFlush();
};

// First-chance exception handler: fires for *every* managed exception, even caught/swallowed ones.
// StackOverflowException and AccessViolationException typically can't be caught — those will still
// kill the process silently, and we rely on the parent's process.Exited handler to log the exit code.
// This filters out benign noise (cancellation) to keep the log readable.
var fceLogged = 0L;
AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
{
    try
    {
        var ex = args.Exception;
        if (ex is OperationCanceledException or System.Threading.Tasks.TaskCanceledException)
            return;

        var typeName = ex.GetType().FullName ?? "unknown";
        // Always log native-boundary signals at high visibility.
        var isNative = typeName.Contains("AccessViolation", StringComparison.OrdinalIgnoreCase)
                       || typeName.Contains("SEHException", StringComparison.OrdinalIgnoreCase)
                       || typeName.Contains("OnnxRuntime", StringComparison.OrdinalIgnoreCase)
                       || typeName.StartsWith("Pty.Net", StringComparison.OrdinalIgnoreCase)
                       || typeName.StartsWith("System.ComponentModel.Win32Exception", StringComparison.OrdinalIgnoreCase);

        if (isNative)
        {
            Log.Warning(ex, "[FirstChance:NATIVE] {Type} pid={Pid}", typeName, Environment.ProcessId);
            return;
        }

        // Rate-limit managed FCE to ~every 100th to avoid drowning the log.
        var n = System.Threading.Interlocked.Increment(ref fceLogged);
        if (n % 100 == 1)
        {
            Log.Information("[FirstChance] {Type} (n={N}) pid={Pid} msg={Msg}",
                typeName, n, Environment.ProcessId,
                ex.Message.Length > 160 ? ex.Message[..160] : ex.Message);
        }
    }
    catch
    {
        // Never let FCE handler throw.
    }
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    var snapshot = ShutdownDiagnostics.GetSnapshot();
    Log.Information(
        "[Shutdown] ProcessExit observed. processId={ProcessId} diagnostics={Diagnostics}",
        Environment.ProcessId,
        ShutdownDiagnostics.FormatSnapshot(snapshot));
    Log.CloseAndFlush();
};

// MCP stdio server mode: `vb mcp`. Speaks MCP over stdin/stdout for CLIs that spawn it
// (claude/codex `mcp add`). No web server, no port, no auth — stdio is inherently scoped to the
// spawning process. Branch out BEFORE any Console/Kestrel setup so stdout stays clean for the
// JSON-RPC stream. The static Serilog file logger configured above still applies.
if (McpStdioHost.IsRequested(args))
{
    await McpStdioHost.RunAsync(args);
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);

// Remove default host logging providers so Microsoft.Extensions.Logging does not
// write into interactive native terminal sessions. File logging still goes
// through the static Serilog logger configured above.
builder.Logging.ClearProviders();

// CreateSlimBuilder doesn't load appsettings.json by default — add it explicitly
builder.Configuration.AddJsonFile(Path.Combine(exeDirectory, "appsettings.json"), optional: false, reloadOnChange: false);
builder.Configuration.AddJsonFile(Path.Combine(exeDirectory, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: false);

// Configure JSON serialization for AOT
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Suppress HTTP request logs for clean CLI interaction
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);

// Configure Kestrel with auto-selected port
int port = PortFinder.FindOpenPort();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
});

// Register DI services
MapRegisterServices.Register(builder.Services, args, $"http://127.0.0.1:{port}");

// Add CORS support for localhost and VSCode webview
builder.Services.AddCors(options =>
{
    options.AddPolicy("VSCodeWebview", policy =>
    {
        policy.WithOrigins($"http://localhost:{port}", $"http://127.0.0.1:{port}")
            .SetIsOriginAllowed(origin =>
                origin.StartsWith("vscode-webview://", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Best-effort cleanup for active PTY sessions on graceful host shutdown.
// This reduces the chance of orphaned child terminal processes.
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        var snapshot = ShutdownDiagnostics.GetSnapshot();
        Log.Information(
            "[Shutdown] ApplicationStopping. processId={ProcessId} diagnostics={Diagnostics}",
            Environment.ProcessId,
            ShutdownDiagnostics.FormatSnapshot(snapshot));

        var tabHost = app.Services.GetService<ITerminalTabHostService>();
        tabHost?.StopAllAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var scope = app.Services.CreateScope();
        var terminalService = scope.ServiceProvider.GetRequiredService<ITerminalSessionService>();

        if (!terminalService.HasActiveSession)
        {
            return;
        }

        if (terminalService.IsExternallyOwned)
        {
            terminalService.UnregisterTerminalAsync().GetAwaiter().GetResult();
            return;
        }

        terminalService.StopSessionAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[Shutdown] Failed to cleanup active terminal session");
    }
});

// Run startup checks (synchronous: DB, settings, global save)
Init.StartUpChecks(app.Services);

// Git detection runs in the background — the server starts immediately.
// The /api/v1/context endpoint reads ParserConfigs which updates when detection completes.
var gitDetectionTask = Init.StartGitDetectionAsync(app.Services, launchDirectory);

// Check for updates (async, non-blocking)
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var updateService = scope.ServiceProvider.GetService<UpdateService>();
        if (updateService != null)
        {
            var updateInfo = await updateService.CheckForUpdateAsync();
            if (updateInfo?.UpdateAvailable == true)
            {
                Log.Information(
                    "[VibeRails] Update available: v{CurrentVersion} -> v{LatestVersion}. Windows install: irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex. Linux install: wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash",
                    updateInfo.CurrentVersion,
                    updateInfo.LatestVersion);
            }
        }
    }
    catch
    {
        // Silently ignore update check failures
    }
});

// Configure middleware and routes BEFORE CliLoop so the app is ready to serve in all modes

// Request logging — first in the pipeline so every request is traced on entry and exit.
// Invaluable when the process dies silently: the last "[HTTP →]" with no matching "[HTTP ←]"
// tells you exactly which endpoint was running at the time of death.
app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    var method = context.Request.Method;
    var path = context.Request.Path.Value ?? string.Empty;
    var query = context.Request.QueryString.Value ?? string.Empty;
    var isWs = context.WebSockets.IsWebSocketRequest;
    var tag = isWs ? "WS" : "HTTP";
    Log.Information("[{Tag} →] {Method} {Path}{Query}", tag, method, path, query);
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        sw.Stop();
        Log.Error(ex, "[{Tag} ✘] {Method} {Path} after {ElapsedMs}ms", tag, method, path, sw.ElapsedMilliseconds);
        throw;
    }
    sw.Stop();
    Log.Information("[{Tag} ←] {Method} {Path} → {Status} ({ElapsedMs}ms)", tag, method, path, context.Response.StatusCode, sw.ElapsedMilliseconds);
});

app.UseCors("VSCodeWebview");
app.UseWebSockets();
app.UseMiddleware<CookieAuthMiddleware>();  // Auth checks happen FIRST

// Static files middleware runs AFTER auth - if auth passes, files are served
if (Directory.Exists(webRootPath))
{
    var fileProvider = new PhysicalFileProvider(webRootPath);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider,
        DefaultFileNames = new List<string> { "index.html" }
    });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.MapApiEndpoints(launchDirectory);

// In-process MCP server over HTTP. Root backend only (terminal-tab children skip both
// the DI registration and this endpoint — see MapRegisterServices). Sits behind
// CookieAuthMiddleware, so callers must present a valid viberails_session token: the
// MCP Explorer forwards the caller's token to its own loopback /mcp, and external CLIs
// would pass it as a request header. /mcp is not under /api, so no per-tab token applies.
if (!MapRegisterServices.IsTerminalTabChildProcess(args))
{
    app.MapMcp("/mcp");
}

// Handle all CLI modes (env, agent, rules, validate, hooks, launch, etc.)
// --env falls through with exit=false so the web server starts alongside the CLI terminal
var (exit, parsedArgs) = await CliLoop.RunAsync(args, app.Services);
if (exit)
{
    return;
}

Log.Information(
    "[Startup] Parsed mode. processId={ProcessId} isVsCodeMode={IsVsCodeMode} isLMBootstrap={IsLMBootstrap} env={Env}",
    Environment.ProcessId,
    parsedArgs.IsVsCodeMode,
    parsedArgs.IsLMBootstrap,
    parsedArgs.LMBootstrapCli ?? "n/a");

// Start server in background (non-blocking)
await app.StartAsync();

// Heartbeat — every 10s, emit uptime + resource stats. If the log has fresh heartbeats
// right before a silent death, that proves the process was healthy until the crash
// point, and gives a rough handle-count trend for leak detection.
var heartbeatStart = DateTimeOffset.UtcNow;
_ = Task.Run(async () =>
{
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                var up = DateTimeOffset.UtcNow - heartbeatStart;
                var handles = proc.HandleCount;
                var threads = proc.Threads.Count;
                var wsMb = proc.WorkingSet64 / 1024 / 1024;
                var gcMb = GC.GetTotalMemory(false) / 1024 / 1024;
                ThreadPool.GetAvailableThreads(out var workerAvail, out var ioAvail);
                Log.Information(
                    "[Heartbeat] up={Uptime} handles={Handles} threads={Threads} workingSetMB={Ws} gcMB={Gc} tpAvail={WorkerAvail}/{IoAvail}",
                    up.ToString(@"hh\:mm\:ss"),
                    handles,
                    threads,
                    wsMb,
                    gcMb,
                    workerAvail,
                    ioAvail);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Heartbeat] sample failed");
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown.
    }
});


string serverUrl = $"http://localhost:{port}";

if (parsedArgs.IsLMBootstrap)
{
    // CLI + Web concurrent mode: terminal runs in foreground, web server in background
    Console.WriteLine($"[VibeRails] Web viewer: {serverUrl}");
    await CliLoop.RunTerminalWithWebAsync(parsedArgs, app.Services);
    await app.StopAsync();
    return;
}

// Standard web-only mode
// Generate one-time bootstrap code for authentication
IAuthBootstrapService authBootstrapService = app.Services.GetRequiredService<IAuthBootstrapService>();
string bootstrapCode = authBootstrapService.GenerateBootstrapCode();
string bootstrapUrl = $"{serverUrl}/auth/bootstrap?code={bootstrapCode}";
string vsCodeV1Url = $"vs-code-v1={bootstrapUrl}";

// Encode user-supplied args (strip internal flags) for pass-through to new instances.
// `--parent-pid` may be passed as two tokens; strip both the flag and its value so
// internal PIDs do not leak into redirectArgs.
var redirectArgs = GetRedirectArgs(args);

if (redirectArgs.Length > 0)
{
    var encodedArgs = Uri.EscapeDataString(string.Join(" ", redirectArgs));
    bootstrapUrl = $"{bootstrapUrl}&redirectArgs={encodedArgs}";
}

// VS Code mode: emit bootstrap URL immediately, then flush so the extension picks it up.
// Don't wait for git detection — the frontend handles the "not in git" state dynamically.
if (parsedArgs.IsVsCodeMode)
{
    Log.Information(
        "[Startup] Running in VS Code mode. processId={ProcessId} serverUrl={ServerUrl}",
        Environment.ProcessId,
        serverUrl);
    Console.WriteLine($"vs-code-v1={bootstrapUrl}");
    Console.Out.Flush();
}
else
{
    // Terminal/browser mode: wait for git detection so rootPath/isInGit are populated
    // before we print the URL. Git is optional — we no longer block (or force-open the
    // browser) when the directory isn't a git repo.
    await gitDetectionTask;

    if (parsedArgs.OpenBrowser)
    {
        LaunchBrowser.Launch(bootstrapUrl);
    }
}

Console.WriteLine($"Vibe Rails server running on {serverUrl}");
Console.WriteLine($"Launch directory: {launchDirectory}");
Console.WriteLine();
Console.WriteLine($"Open this URL to access the dashboard:");
Console.WriteLine($"  {bootstrapUrl}");
Console.WriteLine();
Console.WriteLine("(Link expires in 2 minutes, can only be used once, and the server stops if unused)");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();


// Wait for shutdown signal (Ctrl+C / SIGTERM / idle-owner watchdog)
await app.WaitForShutdownAsync(app.Lifetime.ApplicationStopping);
Log.Information(
    "[Shutdown] WaitForShutdownAsync completed. processId={ProcessId} diagnostics={Diagnostics}",
    Environment.ProcessId,
    ShutdownDiagnostics.FormatSnapshot(ShutdownDiagnostics.GetSnapshot()));

static string[] GetRedirectArgs(string[] args)
{
    // Strip internal/web-only flags so they don't leak into a redirected vb invocation.
    // Surviving redirectable shape: `--env <x> --workdir <y> [-- ...]`.
    var redirectArgs = new List<string>(args.Length);

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                i++;
            }
            continue;
        }

        if (arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("--vs-code-v1", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("--web", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        redirectArgs.Add(arg);
    }

    return redirectArgs.ToArray();
}
