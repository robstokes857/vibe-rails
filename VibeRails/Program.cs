using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Serilog;
using VibeRails;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
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
        Log.Fatal(exception, "[Shutdown] Unhandled exception. terminating={IsTerminating}", eventArgs.IsTerminating);
        return;
    }

    Log.Fatal(
        "[Shutdown] Unhandled non-Exception object. type={Type} terminating={IsTerminating}",
        typeName,
        eventArgs.IsTerminating);
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
MapRegisterServices.Register(builder.Services);

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

// Handle all CLI modes (env, agent, rules, validate, hooks, launch, etc.)
// --env falls through with exit=false so the web server starts alongside the CLI terminal
var (exit, parsedArgs) = await CliLoop.RunAsync(args, app.Services);
if (exit)
{
    return;
}

Log.Information(
    "[Startup] Parsed mode. processId={ProcessId} isVsCodeMode={IsVsCodeMode} isLMBootstrap={IsLMBootstrap} cli={Cli}",
    Environment.ProcessId,
    parsedArgs.IsVsCodeMode,
    parsedArgs.IsLMBootstrap,
    parsedArgs.Cli ?? "n/a");

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
IAuthService authService = app.Services.GetRequiredService<IAuthService>();
string bootstrapCode = authService.GenerateBootstrapCode();
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

var isVsCodeMode = args.Any(a => a.Contains("--vs"));

// VS Code mode: emit bootstrap URL immediately, then flush so the extension picks it up.
// Don't wait for git detection — the frontend handles the "not in git" state dynamically.
if (isVsCodeMode)
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
    // Terminal/browser mode: wait for git detection so we can decide whether to launch the browser.
    var status = await gitDetectionTask;

    if (status == StartUpStatus.RequirementsNotMet_NotInGIT)
    {
        Console.WriteLine($"[VibeRails] Not in a git repository. Opening browser to fix...");
        LaunchBrowser.Launch(bootstrapUrl);
    }
    else if (args.Any(a => a is "--open-browser" or "--launch-browser" or "--launch-web" or "--web"))
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
Console.WriteLine("(Link expires in 2 minutes and can only be used once)");
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
            || arg.StartsWith("--vs", StringComparison.OrdinalIgnoreCase)
            || arg is ("--open-browser" or "--launch-browser" or "--launch-web" or "--web"))
        {
            continue;
        }

        redirectArgs.Add(arg);
    }

    return redirectArgs.ToArray();
}

