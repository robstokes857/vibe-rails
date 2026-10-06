using System.Diagnostics.CodeAnalysis;

namespace VibeRails.Services.LocalFront;

/// <summary>
/// What this process decided about local Front mode (VB-8NI09-170). <see cref="Requested"/> is
/// true whenever <see cref="LocalFrontMode.OriginVariable"/> is set, valid or not. Only a valid
/// request in a Debug build running in Development is <see cref="Active"/>.
/// </summary>
public sealed record LocalFrontState(bool Requested, Uri? Origin, string? Error)
{
    /// <summary>No local mode: the variable is not set.</summary>
    public static readonly LocalFrontState Off = new(false, null, null);

    /// <summary>The desktop talks to the local Front stack at <see cref="Origin"/>.</summary>
    [MemberNotNullWhen(true, nameof(Origin), nameof(OriginText))]
    public bool Active => Requested && Origin is not null && Error is null;

    /// <summary>The origin without a trailing slash, e.g. <c>https://localhost:5164</c>.</summary>
    public string? OriginText => Origin?.GetLeftPart(UriPartial.Authority);
}

/// <summary>
/// Process-scoped switch between the production Front (viberails.ai) and a Front stack running on
/// this machine (VibeRails-Front <c>run.ps1 start</c>). It is selected only by the
/// <see cref="OriginVariable"/> environment variable, which the "VibeRails + Local Front" Visual
/// Studio profile sets. Nothing is persisted: the next process without the variable is a normal
/// one, with its saved key, sync and uploads untouched.
///
/// Two rules, both fail closed:
/// <list type="bullet">
/// <item>Any process that sees the variable, valid or not, publishes nothing to production
///   (Board sync, session upload, complete backups, token savings, signing-key sync).</item>
/// <item>Only a valid loopback https origin in a Debug build under Development activates local
///   routing. Anything else stops the web host at startup instead of quietly running normally.</item>
/// </list>
/// </summary>
public static class LocalFrontMode
{
    /// <summary>The local Front origin, e.g. <c>https://localhost:5164</c>.</summary>
    public const string OriginVariable = "VIBERAILS_LOCAL_FRONT_ORIGIN";

    /// <summary>The origin Front's local Docker stack serves (docs/local-desktop-development.md).</summary>
    public const string DefaultOrigin = "https://localhost:5164";

    /// <summary>The production Front origin shipped in appsettings.json.</summary>
    public const string ProductionOrigin = "https://viberails.ai";

    // Uri lowercases host names, so an ordinal set is exact.
    private static readonly HashSet<string> SupportedHosts = new(StringComparer.Ordinal) { "localhost", "127.0.0.1", "[::1]" };

    private static readonly Lazy<LocalFrontState> s_process = new(FromProcessEnvironment);
    // Tests scope an override to their own async flow so parallel test classes keep seeing Off.
    private static readonly AsyncLocal<LocalFrontState?> s_override = new();

    /// <summary>This process's decision, read once from its environment.</summary>
    public static LocalFrontState Current => s_override.Value ?? s_process.Value;

    /// <summary>True when Front traffic goes to the local stack.</summary>
    public static bool IsActive => Current.Active;

    /// <summary>
    /// True when production publishing must not run in this process. The variable alone is
    /// enough: a refused (Release, invalid) request still never reaches production.
    /// </summary>
    public static bool PausesProductionPublishing => Current.Requested;

    /// <summary>True in a Debug build of VibeRails; local mode never activates otherwise.</summary>
    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// The user-facing reason a production-only feature did nothing. Never contains a credential.
    /// </summary>
    public static string PausedMessage(string feature)
    {
        var state = Current;
        return state.Active
            ? $"{feature} is paused while this VibeRails process uses the local Front at {state.OriginText}. Start VibeRails with a normal profile to use it."
            : $"{feature} is paused while {OriginVariable} is set for this process.";
    }

    /// <summary>
    /// Decides local mode from the variable's raw value, the build and the host environment name.
    /// A null or blank value means the variable is not set.
    /// </summary>
    public static LocalFrontState Resolve(string? rawOrigin, bool debugBuild, string? environmentName)
    {
        if (string.IsNullOrWhiteSpace(rawOrigin))
            return LocalFrontState.Off;
        if (!debugBuild)
            return new(true, null,
                $"{OriginVariable} is set, but local Front mode runs only in a Debug build of VibeRails. Unset it, or start the Debug build from Visual Studio.");
        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            return new(true, null,
                $"{OriginVariable} is set, but local Front mode requires ASPNETCORE_ENVIRONMENT=Development (this process runs in {(string.IsNullOrWhiteSpace(environmentName) ? "Production" : environmentName)}).");
        return TryParseOrigin(rawOrigin, out var origin, out var error)
            ? new(true, origin, null)
            : new(true, null, error);
    }

    /// <summary>
    /// Accepts only an https origin on <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>, with no
    /// user information, path, query or fragment. Returns it normalized with a trailing slash. The
    /// dashboard's sign-in panel accepts the same three hosts (remote-account-link.js).
    /// </summary>
    public static bool TryParseOrigin(string? raw, [NotNullWhen(true)] out Uri? origin, [NotNullWhen(false)] out string? error)
    {
        origin = null;
        var text = raw?.Trim();
        var invalid = $"{OriginVariable} must be an https origin on localhost, such as {DefaultOrigin}.";
        // Uri quietly turns backslashes into slashes and drops some whitespace; refuse them first.
        if (string.IsNullOrEmpty(text) || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = invalid;
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"{OriginVariable} must use https: the local Front serves the trusted development certificate. Got {uri.Scheme}.";
            return false;
        }
        if (uri.UserInfo.Length != 0 || text.Contains('@'))
        {
            error = $"{OriginVariable} must not contain user information.";
            return false;
        }
        if (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || text.Contains('?') || text.Contains('#'))
        {
            error = $"{OriginVariable} must be an origin with no path, query or fragment, such as {DefaultOrigin}.";
            return false;
        }
        if (!SupportedHosts.Contains(uri.Host))
        {
            error = $"{OriginVariable} must name localhost, 127.0.0.1 or [::1]; {uri.Host} is not supported.";
            return false;
        }
        origin = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        error = null;
        return true;
    }

    /// <summary>
    /// True when a configured <c>VibeRails:FrontendUrl</c> can be replaced by the local origin:
    /// the shipped production value, or already the local origin. Anything else is a conflicting
    /// override, and local mode refuses rather than guess which one was meant.
    /// </summary>
    public static bool IsCompatibleFrontendSetting(string? configured, LocalFrontState state)
    {
        if (!state.Active)
            return false;
        var value = configured?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(value)
            || string.Equals(value, ProductionOrigin, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, state.OriginText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Overrides <see cref="Current"/> for the caller's async flow only. Tests only.</summary>
    internal static IDisposable UseForTests(LocalFrontState state)
    {
        var previous = s_override.Value;
        s_override.Value = state;
        return new Restore(previous);
    }

    private static LocalFrontState FromProcessEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable(OriginVariable),
            IsDebugBuild,
            // The same precedence WebApplication uses: ASPNETCORE_ wins over DOTNET_.
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? "Production");

    private sealed class Restore(LocalFrontState? previous) : IDisposable
    {
        public void Dispose() => s_override.Value = previous;
    }
}
