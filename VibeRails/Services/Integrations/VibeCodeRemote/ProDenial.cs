using System.Net;
using System.Text.Json;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>The four hosted feature families behind the Pro plan. Everything local stays free.</summary>
public static class ProFeatures
{
    public const string RemoteBoards = "remote_boards";
    public const string RemoteTerminals = "remote_terminals";
    public const string SessionSharing = "session_sharing";
    public const string CardSharing = "card_sharing";

    public static bool IsKnown(string? feature) => feature is RemoteBoards or RemoteTerminals or SessionSharing or CardSharing;
}

/// <summary>
/// The hosted service's typed "this needs Pro" answer: HTTP 403 with header
/// <c>X-VibeRails-Denial: pro_required</c> and a JSON body naming the feature and who pays.
/// Only those bounded fields are read. Remote prose and the upgrade path are never used: every
/// message and link the desktop shows is its own. See ProPlan.md.
/// </summary>
public sealed record ProDenial(string Feature, string Payer)
{
    public const string Code = "pro_required";
    public const string HeaderName = "X-VibeRails-Denial";
    /// <summary>Close reason of a live relay socket whose owner lost Pro (close code 1008).</summary>
    public const string CloseReason = "Vibe Rails Pro required";
    public const string AccountPayer = "account";
    /// <summary>The caller is an invited member of a Board whose owner is not Pro; the caller is never asked to upgrade.</summary>
    public const string BoardOwnerPayer = "board_owner";
    public const int MaxBodyBytes = 4096;

    /// <summary>True when this account's own plan refused the call, so its loops stop until the plan changes.</summary>
    public bool AccountPays => Payer == AccountPayer;

    public static bool HasHeader(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden
        && response.Headers.TryGetValues(HeaderName, out var values) && values.Contains(Code);

    /// <summary>The refused WebSocket handshake's headers; its body cannot be read through ClientWebSocket.</summary>
    public static bool HasHeader(IReadOnlyDictionary<string, IEnumerable<string>>? headers) =>
        headers is not null && headers.Any(header => string.Equals(header.Key, HeaderName, StringComparison.OrdinalIgnoreCase)
            && header.Value.Contains(Code));

    /// <summary>
    /// The denial named by a 403's header or already-parsed body fields, or null for any other 403.
    /// A missing or unrecognised feature is the caller's own; a missing payer is the account.
    /// </summary>
    public static ProDenial? From(bool header, string? code, string? feature, string? payer, string defaultFeature) =>
        header || code == Code
            ? new(ProFeatures.IsKnown(feature) ? feature! : defaultFeature, payer == BoardOwnerPayer ? BoardOwnerPayer : AccountPayer)
            : null;

    /// <summary>Reads a 403's header and at most <see cref="MaxBodyBytes"/> of its body. Null for anything else.</summary>
    public static async Task<ProDenial?> ReadAsync(HttpResponseMessage response, string defaultFeature, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return null;
        string? code = null, feature = null, payer = null;
        try
        {
            var bytes = await SessionSharingService.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), MaxBodyBytes, ct);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                code = Text(document.RootElement, "code");
                feature = Text(document.RootElement, "feature");
                payer = Text(document.RootElement, "payer");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            // An unreadable or oversized body is not a typed denial; the header alone still is.
        }
        return From(HasHeader(response), code, feature, payer, defaultFeature);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
