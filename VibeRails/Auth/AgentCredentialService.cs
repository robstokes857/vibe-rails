using System.Security.Cryptography;
using System.Text;

namespace VibeRails.Auth;

/// <summary>Short-lived contacts for CLI descendants, separate from dashboard credentials.</summary>
public sealed class AgentCredentialService
{
    /// <summary>A paired capability for one tab launch or process-local proxy session.</summary>
    public sealed record Contact(string Session, string Tab, string Owner, bool Proxy, string? SessionId = null);
    private readonly object gate = new();
    private readonly Dictionary<string, Contact> contacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Contact> pendingTools = new(StringComparer.Ordinal);
    private Contact? proxy;

    /// <summary>Gets the capability associated with a tab or native session.</summary>
    public Contact ForTools(string owner)
    {
        lock (gate)
        {
            var existing = contacts.Values.FirstOrDefault(c => !c.Proxy && c.Owner == owner);
            return existing ?? Create(owner, false);
        }
    }

    /// <summary>Prepares an unbound replacement while preserving the running session's contact.</summary>
    public Contact PrepareTools(string owner)
    {
        lock (gate)
        {
            if (pendingTools.Remove(owner, out var previous)) contacts.Remove(previous.Session);
            var contact = Create(owner, false);
            pendingTools[owner] = contact;
            return contact;
        }
    }

    /// <summary>Discards a rejected start's candidate without affecting the active contact.</summary>
    public void CancelPreparedTools(string owner)
    {
        lock (gate)
            if (pendingTools.Remove(owner, out var candidate)) contacts.Remove(candidate.Session);
    }

    /// <summary>The current process-local proxy capability, never a dashboard secret.</summary>
    public Contact ProxyContact
    {
        get { lock (gate) return proxy ??= Create("unstarted", true); }
    }

    /// <summary>Replaces the proxy capability before preparing a new CLI environment.</summary>
    public void BeginSession(string sessionId)
    {
        lock (gate)
        {
            if (proxy is not null) contacts.Remove(proxy.Session);
            proxy = Create(sessionId, true);
        }
    }

    /// <summary>Revokes all capabilities for an ended session or removed tab.</summary>
    public void Revoke(string owner)
    {
        lock (gate)
        {
            foreach (var c in contacts.Values.Where(c => c.Owner == owner).ToArray())
                contacts.Remove(c.Session);
            pendingTools.Remove(owner);
            if (proxy?.Owner == owner) proxy = null;
        }
    }

    /// <summary>Binds the root-issued capability to the child's confirmed session.</summary>
    public void BindSession(string owner, string sessionId)
    {
        lock (gate)
        {
            var contact = pendingTools.Remove(owner, out var candidate) ? candidate
                : contacts.Values.FirstOrDefault(c => !c.Proxy && c.Owner == owner);
            if (contact is null) return;
            foreach (var previous in contacts.Values.Where(c => !c.Proxy && c.Owner == owner).ToArray())
                contacts.Remove(previous.Session);
            contacts[contact.Session] = contact with { SessionId = sessionId };
        }
    }

    /// <summary>Validates both values against the same live capability.</summary>
    public Contact? Authenticate(string? session, string? tab)
    {
        lock (gate)
            return session is not null && contacts.TryGetValue(session, out var contact)
                && tab is not null && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(contact.Tab), Encoding.UTF8.GetBytes(tab)) ? contact : null;
    }

    /// <summary>Identifies a scoped token so failed scope checks cannot fall back to dashboard auth.</summary>
    public static bool IsAgentToken(string? token) => token?.StartsWith("vr_agent_", StringComparison.Ordinal) == true;

    /// <summary>Checks the exact method/path capability before a business handler executes.</summary>
    public static bool Allows(Contact contact, HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        if (contact.Proxy)
            return path.StartsWith("/llm/anthropic/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/llm/openai/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/llm/zai/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/llm/xai/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/llm/cli-chat/", StringComparison.OrdinalIgnoreCase)
                || (HttpMethods.IsPost(request.Method) && path is "/llm/control/token-saver/pause" or "/llm/control/token-saver/resume" or "/llm/control/agent/end-session")
                || (HttpMethods.IsGet(request.Method) && path == "/llm/control/token-saver/status");

        if (HttpMethods.IsGet(request.Method))
            return path is "/api/v1/llm-picker/preferences" or "/api/v1/session-sharing/capabilities";
        if (!HttpMethods.IsPost(request.Method)) return false;
        var segments = path.Split('/');
        if (segments.Length == 6 && segments[1] == "api" && segments[2] == "v1"
            && segments[3] == "sessions" && Guid.TryParseExact(segments[4], "D", out _)
            && segments[5] == "sharing-links") return true;
        return segments.Length == 7 && segments[1] == "api" && segments[2] == "v1"
            && segments[3] == "board" && segments[4] == "cards"
            && segments[5].Length is > 0 and <= 256 && segments[5].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            && segments[6] == "launch";
    }

    private Contact Create(string owner, bool isProxy)
    {
        static string Token() => "vr_agent_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var contact = new Contact(Token(), Token(), owner, isProxy);
        contacts.Add(contact.Session, contact);
        return contact;
    }
}
