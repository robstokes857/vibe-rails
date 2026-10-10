namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Who may open a card or session sharing link: anyone holding it, or only people the owner lists
/// by email. This validates the choice locally before anything is sent, with fixed wording; the
/// hosted service enforces the same rules and the per-card/per-session limit across links.
/// </summary>
public static class ShareAudience
{
    public const int RecipientLimit = 10;
    public const string Public = "public";
    public const string Email = "email";

    /// <summary>Normalizes the requested audience, or explains why it is invalid. Addresses are never looked up.</summary>
    public static bool TryNormalize(string? access, IEnumerable<string?>? emails, out string mode, out IReadOnlyList<string> addresses, out string error)
    {
        mode = Public; addresses = []; error = "";
        var requested = access?.Trim().ToLowerInvariant();
        var supplied = (emails ?? []).Select(e => e?.Trim() ?? "").Where(e => e.Length > 0).ToList();
        if (requested is null or "" or Public)
        {
            if (supplied.Count == 0) return true;
            error = "A public link does not take email addresses. Choose \"Only people I list\" to use them.";
            return false;
        }
        if (requested != Email)
        {
            error = "Choose who can view: anyone with the link, or only people you list.";
            return false;
        }
        var list = new List<string>();
        foreach (var address in supplied)
        {
            var at = address.IndexOf('@');
            if (address.Length > 320 || at <= 0 || at == address.Length - 1 || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                error = "Enter valid email addresses (up to 320 characters each), one per line or comma-separated.";
                return false;
            }
            if (list.Any(existing => string.Equals(existing, address, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(address);
            if (list.Count > RecipientLimit)
            {
                error = $"Up to {RecipientLimit} people can be listed per link, and {RecipientLimit} per card or session across its active links.";
                return false;
            }
        }
        if (list.Count == 0)
        {
            error = "List at least one email address, or make the link public.";
            return false;
        }
        mode = Email; addresses = list;
        return true;
    }

    /// <summary>
    /// The hosted link must confirm the requested audience. A server without the sharing update
    /// ignores the fields and would silently create a public link, which must never pass as restricted.
    /// </summary>
    public static bool Confirms(string requestedMode, string? returnedAccess)
        => requestedMode == Public ? returnedAccess is null or "" or Public : returnedAccess == Email;
}
