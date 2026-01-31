using Microsoft.Extensions.FileProviders;
using VibeRails;
using VibeRails.DTOs;
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

// Configure Kestrel with auto-selected port
int port = PortFinder.FindOpenPort();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
});

builder.Services.AddOpenApi();

// Register DI services
Init.RegisterServices(builder.Services);

// Add CORS support for VS Code webview
builder.Services.AddCors(options =>
{
    options.AddPolicy("VSCodeWebview", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            origin.StartsWith("vscode-webview://") ||
            origin.StartsWith("http://localhost") ||
            origin.StartsWith("https://localhost"))
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Run startup checks
await Init.StartUpChecks(app.Services);



// Handle all CLI modes (env, agent, rules, validate, hooks, launch, --lmbootstrap, --validate-vca, etc.)
var (exit, _) = await CliLoop.RunAsync(args, app.Services);
if (exit)
{
    return;
}

if (!Directory.Exists(webRootPath))
{
    throw new Exception("webroot not found");
}

var fileProvider = new PhysicalFileProvider(webRootPath);

// Enable CORS for VS Code webview
app.UseCors("VSCodeWebview");

// Enable WebSockets for terminal
app.UseWebSockets();

// Enable default files (index.html) - must be before UseStaticFiles
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = fileProvider,
    DefaultFileNames = new List<string> { "index.html" }
});

app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });

// Map API endpoints
app.MapApiEndpoints(launchDirectory);
app.MapOpenApi();


// Start server in background (non-blocking)
await app.StartAsync();

string serverUrl = $"http://localhost:{port}";
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



