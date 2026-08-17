namespace VibeRails;

/// <summary>
/// What this process is, resolved once from argv during service registration and published as a
/// singleton.
///
/// Route mapping and DI registration both gate on this. They used to derive it independently —
/// <see cref="MapRegisterServices.Register"/> from the <c>args</c> it was handed, route mapping
/// from <see cref="Environment.GetCommandLineArgs"/>, which is a different array (its first element
/// is the executable path). The two predicates happen to agree today because both only scan for
/// flags, but nothing held them together, and the failure mode when they drifted would have been a
/// route mapped against a service that was never registered: a 500 in a process role that should
/// have returned 404. Reading the answer instead of recomputing it removes the possibility.
/// </summary>
public sealed record ProcessRole(bool IsActiveRootBackend, bool IsTerminalTabChild);
