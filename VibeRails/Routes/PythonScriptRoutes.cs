using VibeRails.DTOs;
using VibeRails.Services.PythonScripts;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

public static class PythonScriptRoutes
{
    public static void Map(WebApplication app, bool isActiveRootBackend)
    {
        // Status reads fail closed (400 + message) while another process briefly holds
        // the signing file, rather than reporting an empty/unsigned state or a raw 500.
        app.MapGet("/api/v1/python-scripts", (
            IPythonScriptService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetStatusAsync(cancellationToken)))
            .WithName("GetPythonScripts");

        app.MapPost("/api/v1/python-scripts/pin", (
            IPythonScriptService service,
            SetPythonScriptPinRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.SetPinAsync(request, cancellationToken)))
            .WithName("SetPythonScriptPin");

        app.MapPost("/api/v1/python-scripts/approve", (
            IPythonScriptService service,
            PythonScriptApprovalRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.ApproveAsync(request, cancellationToken)))
            .WithName("ApprovePythonScript");

        app.MapPost("/api/v1/python-scripts/revoke", (
            IPythonScriptService service,
            PythonScriptApprovalRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RevokeAsync(request, cancellationToken)))
            .WithName("RevokePythonScript");

        app.MapPost("/api/v1/python-scripts/run", (
            IPythonScriptService service,
            PythonScriptRunRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RunAsync(request.Name, cancellationToken)))
            .WithName("RunPythonScript");

        app.MapGet("/api/v1/python-scripts/mcp", (
            IPythonScriptMcpService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetAsync(cancellationToken)))
            .WithName("GetPythonScriptMcpConfigurations");

        app.MapPut("/api/v1/python-scripts/mcp", (
            IPythonScriptMcpService service,
            PythonScriptMcpConfigurationRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.SaveAsync(request, cancellationToken)))
            .WithName("SavePythonScriptMcpConfiguration");

        app.MapDelete("/api/v1/python-scripts/mcp", (
            IPythonScriptMcpService service,
            string? name,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.DeleteAsync(name, cancellationToken)))
            .WithName("DeletePythonScriptMcpConfiguration");

        if (isActiveRootBackend)
        {
            app.MapPost("/api/v1/python-scripts/run/interactive", async (
                ITerminalTabHostService tabHost,
                PythonScriptRunRequest request,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var tab = await tabHost.CreatePythonScriptTabAsync(request.Name ?? string.Empty, cancellationToken);
                    return Results.Ok(new PythonScriptInteractiveRunResponse(
                        request.Name?.Trim() ?? string.Empty,
                        tab.TabId,
                        "Python script started in an interactive terminal."));
                }
                catch (PythonScriptValidationException exception)
                {
                    return Results.BadRequest(new ErrorResponse(exception.Message));
                }
                catch (InvalidOperationException exception)
                {
                    return Results.BadRequest(new ErrorResponse(exception.Message));
                }
            }).WithName("RunPythonScriptInteractive");
        }

        app.MapGet("/api/v1/python-scripts/runs", (IPythonScriptService service) =>
            Results.Ok(service.GetRunHistory()))
            .WithName("GetPythonScriptRuns");

        // Authoring. None of these take a PIN, and none of them can make a script runnable:
        // every write moves the file's canonical hash away from whatever was approved. Only
        // approve/revoke above touch the signing document's trust.
        app.MapGet("/api/v1/python-scripts/content", (
            IPythonScriptService service,
            string? name,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.GetContentAsync(name, cancellationToken)))
            .WithName("GetPythonScriptContent");

        app.MapPost("/api/v1/python-scripts/content", (
            IPythonScriptService service,
            PythonScriptSaveRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.SaveContentAsync(request, cancellationToken)))
            .WithName("SavePythonScriptContent");

        app.MapPost("/api/v1/python-scripts/create", (
            IPythonScriptService service,
            PythonScriptSaveRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.CreateAsync(request, cancellationToken)))
            .WithName("CreatePythonScript");

        app.MapPost("/api/v1/python-scripts/rename", (
            IPythonScriptService service,
            PythonScriptRenameRequest request,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.RenameAsync(request, cancellationToken)))
            .WithName("RenamePythonScript");

        app.MapDelete("/api/v1/python-scripts", (
            IPythonScriptService service,
            string? name,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.DeleteAsync(name, cancellationToken)))
            .WithName("DeletePythonScript");

        // Import reads a caller-supplied path from anywhere on the host, so it is a root
        // dashboard capability like the file picker that feeds it (see FileSystemRoutes) and
        // is not exposed from the short-lived backend behind every terminal tab.
        if (isActiveRootBackend)
        {
            app.MapPost("/api/v1/python-scripts/import", (
                IPythonScriptService service,
                PythonScriptImportRequest request,
                CancellationToken cancellationToken) =>
                ExecuteAsync(() => service.ImportAsync(request, cancellationToken)))
                .WithName("ImportPythonScript");
        }
    }

    private static async Task<IResult> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (PythonScriptValidationException exception)
        {
            return Results.BadRequest(new ErrorResponse(exception.Message));
        }
    }
}
