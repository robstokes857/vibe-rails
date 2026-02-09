using Microsoft.Extensions.FileProviders;
using VibeRails;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Utils;


// Capture launch directory FIRST (where the user ran the command from)
string launchDirectory = Directory.GetCurrentDirectory();

// Get the executable's directory (where wwwroot lives)
string exeDirectory = AppContext.BaseDirectory;
string webRootPath = Path.Combine(exeDirectory, "wwwroot");

var builder = WebApplication.CreateSlimBuilder(args);

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

builder.Services.AddOpenApi();

// Register DI services
Init.RegisterServices(builder.Services);

// Add CORS support for VS Code webview and local file access
builder.Services.AddCors(options =>
{
    options.AddPolicy("VSCodeWebview", policy =>
    {
        policy.AllowAnyOrigin() // Allow all origins including null (file://)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

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
                Console.WriteLine($"[VibeRails] Run 'vb update' to install the latest version.");
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
app.MapOpenApi();

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
Console.WriteLine($"Vibe Rails server running on {serverUrl}");
Console.WriteLine($"Launch directory: {launchDirectory}");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

// Launch browser only if --open-browser flag is passed
if (args.Contains("--open-browser"))
{
    LaunchBrowser.Launch(serverUrl);
}

// Wait for shutdown signal (Ctrl+C)
await app.WaitForShutdownAsync();
