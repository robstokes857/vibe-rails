using VibeRails.DTOs;
using VibeRails.Services.FileSystem;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class FileSystemRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/filesystem/entries", (
            IFileSystemBrowserService browser,
            string? path,
            bool? includeHidden,
            string? search,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var defaultPath = ResolveDefaultPath(launchDirectory);
                return Results.Ok(browser.Browse(
                    path,
                    defaultPath,
                    includeHidden ?? false,
                    search,
                    cursor,
                    pageSize,
                    cancellationToken));
            }
            catch (FileSystemBrowseException ex)
            {
                var error = new ErrorResponse(ex.Message);
                return ex.Error switch
                {
                    FileSystemBrowseError.NotFound => Results.NotFound(error),
                    FileSystemBrowseError.AccessDenied => Results.Json(
                        error,
                        statusCode: StatusCodes.Status403Forbidden),
                    _ => Results.BadRequest(error)
                };
            }
        }).WithName("BrowseFileSystemEntries");
    }

    internal static string ResolveDefaultPath(string launchDirectory)
    {
        var configuredRoot = ParserConfigs.GetRootPath();
        return string.IsNullOrWhiteSpace(configuredRoot) ? launchDirectory : configuredRoot;
    }
}
