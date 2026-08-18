using VibeRails.DTOs;
using VibeRails.Services.PythonScripts;

namespace VibeRails.Routes;

public static class PythonScriptRoutes
{
    public static void Map(WebApplication app)
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

        app.MapPost("/api/v1/python-scripts/run", async (
            IPythonScriptService service,
            PythonScriptRunRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.RunAsync(request.Name, cancellationToken));
            }
            catch (PythonScriptValidationException exception)
            {
                return Results.BadRequest(new ErrorResponse(exception.Message));
            }
        }).WithName("RunPythonScript");

        app.MapGet("/api/v1/python-scripts/runs", (IPythonScriptService service) =>
            Results.Ok(service.GetRunHistory()))
            .WithName("GetPythonScriptRuns");
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<PythonScriptListResponse>> operation)
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
