using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration.Json;

namespace VibeRails.Utils;

/// <summary>
/// Builds the backend's web host so that only the install directory and the process's own
/// environment and arguments configure it. The folder VibeRails is started in is a project the
/// user opened; it must never be a configuration source or decide what the server listens on.
/// </summary>
public static class BackendHost
{
    /// <summary>
    /// Creates the builder with its content root pinned to <paramref name="installDirectory"/>.
    /// The slim builder's defaults read <c>appsettings.json</c> and
    /// <c>appsettings.{Environment}.json</c> from the content root with reload-on-change, and the
    /// content root defaults to the current directory. Those defaults are replaced by the install
    /// directory's files, read once.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args, string installDirectory)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = installDirectory
        });

        foreach (var source in builder.Configuration.Sources.OfType<JsonConfigurationSource>().ToArray())
            builder.Configuration.Sources.Remove(source);

        // Added last, so a key the install file defines cannot be overridden by the environment.
        builder.Configuration.AddJsonFile(Path.Combine(installDirectory, "appsettings.json"), optional: false, reloadOnChange: false);
        builder.Configuration.AddJsonFile(Path.Combine(installDirectory, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: false);
        return builder;
    }

    /// <summary>
    /// The one production listener (API_SEC.md, "Listener topology"): loopback on
    /// <paramref name="port"/>. The empty loader replaces the default one bound to the
    /// <c>Kestrel</c> configuration section, so no configuration value can add an endpoint.
    /// </summary>
    public static void ListenOnLoopbackOnly(KestrelServerOptions options, int port)
    {
        options.Configure();
        options.ListenLocalhost(port);
    }
}
