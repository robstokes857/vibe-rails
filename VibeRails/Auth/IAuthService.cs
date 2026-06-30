namespace VibeRails.Auth;

public interface IAuthService
{
    string GetInstanceToken();
    string GetTabToken();
    bool ValidateToken(string? token);
    string GenerateBootstrapCode();
    bool ValidateAndConsumeBootstrapCode(string? code);
    bool TryGetUnconsumedBootstrapCodeExpiryUtc(out DateTime expiryUtc);
    bool TryExpireUnconsumedBootstrapCode(DateTime expectedExpiryUtc);
    bool ValidateTabToken(string? token);
    void SetTabTokenHeader(HttpContext context);
    string ReplaceTabInHtmlString(string html);
}
