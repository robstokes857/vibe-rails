using TokenSaver;
using VibeRails.Auth;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Adapts the app's auth service to the TokenSaver library's slim proxy auth gate, so the library
/// never depends on <see cref="IAuthService"/>'s much wider app-coupled surface.
/// </summary>
public sealed class LlmProxyAuthGateAdapter(IAuthService authService) : ILlmProxyAuthGate
{
    public bool ValidateSessionToken(string? token) => authService.ValidateToken(token);

    public bool ValidateTabToken(string? token) => authService.ValidateTabToken(token);
}
