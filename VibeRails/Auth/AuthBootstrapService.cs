namespace VibeRails.Auth;

public interface IAuthBootstrapService
{
    string GenerateBootstrapCode();
}

public sealed class AuthBootstrapService(
    IAuthService authService,
    IUnconsumedBootstrapCodeShutdownWatchdog watchdog) : IAuthBootstrapService
{
    public string GenerateBootstrapCode()
    {
        var code = authService.GenerateBootstrapCode();
        watchdog.Start();
        return code;
    }
}
