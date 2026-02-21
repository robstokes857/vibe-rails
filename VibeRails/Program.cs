using Microsoft.Extensions.FileProviders;
using Serilog;
using VibeRails;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Services.Tracing;
using VibeRails.Utils;


// Capture launch directory FIRST (where the user ran the command from)
string launchDirectory = Directory.GetCurrentDirectory();

// Get the executable's directory (where wwwroot lives)
string exeDirectory = AppContext.BaseDirectory;
string webRootPath = Path.Combine(exeDirectory, "wwwroot");

// Configure Serilog — file sink to ~/.vibe_rails/logs/
var logDir = Path.Combine(PathConstants.GetInstallDirPath(), "logs");
Directory.CreateDirectory(logDir);
var traceBuffer = new TraceEventBuffer();
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logDir, "vb-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Sink(new TraceSerilogSink(traceBuffer))
    .CreateLogger();

var builder = WebApplication.CreateSlimBuilder(args);

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

// Register trace buffer (created before Serilog for the sink)
builder.Services.AddSingleton(traceBuffer);

// Register DI services
Init.RegisterServices(builder.Services);

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

// Run startup checks
await Init.StartUpChecks(app.Services);

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
                Console.WriteLine($"[VibeRails] Update available: v{updateInfo.CurrentVersion} -> v{updateInfo.LatestVersion}");
                Console.WriteLine("[VibeRails] Install latest version:");
                Console.WriteLine("[VibeRails]   Windows: irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex");
                Console.WriteLine("[VibeRails]   Linux:   wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash");
            }
        }
    }
    catch
    {
        // Silently ignore update check failures
    }
});

// Configure middleware and routes BEFORE CliLoop so the app is ready to serve in all modes
app.UseCors("VSCodeWebview");
app.UseWebSockets();
app.UseMiddleware<CookieAuthMiddleware>();  // Auth checks happen FIRST
app.UseMiddleware<TraceHttpMiddleware>();   // Trace all API requests

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

// Start server in background (non-blocking)
await app.StartAsync();
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
var authService = app.Services.GetRequiredService<IAuthService>();
var bootstrapCode = authService.GenerateBootstrapCode();
var bootstrapUrl = $"{serverUrl}/auth/bootstrap?code={bootstrapCode}";

Console.WriteLine($"Vibe Rails server running on {serverUrl}");
Console.WriteLine($"Launch directory: {launchDirectory}");
Console.WriteLine();
Console.WriteLine($"Open this URL to access the dashboard:");
Console.WriteLine($"  {bootstrapUrl}");
Console.WriteLine();
Console.WriteLine("(Link expires in 2 minutes and can only be used once)");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

if (args.Any(a => a.Contains( "--vs")))
{
    //add a switch later for versions
    Console.WriteLine($"vs-code-v1={bootstrapUrl}");
}

// Launch browser if any browser flag is passed
if (args.Any(a => a is "--open-browser" or "--launch-browser" or "--launch-web" or "--web"))
{
    LaunchBrowser.Launch(bootstrapUrl);
}

// Wait for shutdown signal (Ctrl+C)
await app.WaitForShutdownAsync(app.Lifetime.ApplicationStopping);
