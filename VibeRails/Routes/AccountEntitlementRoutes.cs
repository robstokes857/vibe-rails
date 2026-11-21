using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

/// <summary>
/// Root-dashboard view of the account's hosted plan, behind the normal session and tab credentials.
/// Both routes always answer 200: an unreachable or older server is the <c>unknown</c> state, which
/// allows everything, and a missing key is <c>signed_out</c>. A local 401 would make the dashboard
/// re-bootstrap its own credentials, so no upstream status is ever passed through.
/// </summary>
public static class AccountEntitlementRoutes
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/account/entitlements");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", async (IAccountEntitlements entitlements, CancellationToken ct) =>
            Results.Json(await entitlements.GetAsync(ct), AppJsonSerializerContext.Default.AccountEntitlements));
        // A forced server read, for the moment the user returns from the Billing page.
        group.MapPost("/refresh", async (IAccountEntitlements entitlements, CancellationToken ct) =>
            Results.Json(await entitlements.GetAsync(ct, refresh: true), AppJsonSerializerContext.Default.AccountEntitlements));
    }
}
