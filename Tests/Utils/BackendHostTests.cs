using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// The backend is started inside a project folder. Nothing in that folder may configure the host
/// or add a listener (VIBE-310): configuration files come from the install directory only, and
/// endpoints come from code only.
/// </summary>
public sealed class BackendHostTests : IDisposable
{
    private readonly string _install = Path.Combine(Path.GetTempPath(), "vb-backend-host-" + Guid.NewGuid().ToString("N"));

    public BackendHostTests()
    {
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "appsettings.json"),
            """{ "VibeRails": { "FrontendUrl": "https://viberails.ai", "InstallDirName": ".vibe_rails" } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_install, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ConfigurationFilesComeFromTheInstallDirectoryOnlyAndAreReadOnce()
    {
        var builder = BackendHost.CreateBuilder([], _install);

        Assert.Equal(Path.TrimEndingDirectorySeparator(_install), Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
        var files = builder.Configuration.Sources.OfType<JsonConfigurationSource>().ToArray();
        Assert.NotEmpty(files);
        Assert.All(files, file =>
        {
            Assert.False(file.ReloadOnChange);
            var root = Assert.IsType<PhysicalFileProvider>(file.FileProvider).Root;
            Assert.Equal(Path.TrimEndingDirectorySeparator(_install), Path.TrimEndingDirectorySeparator(root));
        });
        Assert.Equal("https://viberails.ai", builder.Configuration["VibeRails:FrontendUrl"]);
    }

    [Fact]
    public void TheCurrentDirectoryIsNotTheContentRoot()
    {
        // The test host's own directory stands in for "the project the user opened".
        var builder = BackendHost.CreateBuilder([], _install);

        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()),
            Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
        Assert.DoesNotContain(builder.Configuration.Sources.OfType<FileConfigurationSource>(), source =>
            source.FileProvider is PhysicalFileProvider provider
            && Path.TrimEndingDirectorySeparator(provider.Root) == Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void AnInstallFileKeyCannotBeOverriddenByAnEarlierSource()
    {
        // Arguments stand in for every source that precedes the install file (environment included).
        var builder = BackendHost.CreateBuilder(["--VibeRails:FrontendUrl=https://elsewhere.example"], _install);

        Assert.Equal("https://viberails.ai", builder.Configuration["VibeRails:FrontendUrl"]);
    }

    [Fact]
    public async Task AKestrelEndpointInConfigurationAddsNoListener()
    {
        var builder = BackendHost.CreateBuilder([], _install);
        builder.Logging.ClearProviders();
        var port = PortFinder.FindOpenPort();
        var extra = PortFinder.FindOpenPort();
        // What an environment variable or a stray settings file would contribute.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Extra:Url"] = $"http://127.0.0.1:{extra}"
        });
        builder.WebHost.ConfigureKestrel(options => BackendHost.ListenOnLoopbackOnly(options, port));

        await using var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var only = Assert.Single(addresses);
            Assert.Equal($"http://localhost:{port}", only);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void ProgramBuildsItsHostThroughBackendHost()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "VibeRails", "Program.cs"));

        Assert.Contains("BackendHost.CreateBuilder(args, exeDirectory)", program, StringComparison.Ordinal);
        Assert.Contains("BackendHost.ListenOnLoopbackOnly(options, port)", program, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"WebApplication\s*\.\s*Create\w*Builder\s*\("), program);
        Assert.DoesNotMatch(new Regex(@"\.Listen(?:Localhost|AnyIP|UnixSocket)?\s*\("), program);
        Assert.DoesNotContain("AddJsonFile", program, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
