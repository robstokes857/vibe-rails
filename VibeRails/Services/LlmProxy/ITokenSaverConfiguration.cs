namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Whether the token saver is switched on for this session at all, ignoring any active pause.
/// Separate from the pause so the control endpoint can distinguish "paused" from "never running" —
/// the effective snapshot on <see cref="TokenSaver.ILlmProxySettingsService"/> reports both as off.
///
/// "This session" means one provider, not any of them: a proxy process serves the single CLI its
/// terminal tab launched, so the answer must come from that provider's saver toggle. Implemented by
/// <see cref="LlmProxySettingsService"/>, which reads the provider recorded on
/// <see cref="ILlmProxySessionState"/> at launch.
/// </summary>
public interface ITokenSaverConfiguration
{
    bool IsConfiguredOn();
}
