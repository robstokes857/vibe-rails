namespace VibeRails.Auth;

public interface IAuthService
{
    string GetInstanceToken();
    bool ValidateToken(string? token);
    string GenerateBootstrapCode();
    bool ValidateAndConsumeBootstrapCode(string? code);
}
