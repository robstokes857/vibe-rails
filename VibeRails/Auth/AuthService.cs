using System.Security.Cryptography;

namespace VibeRails.Auth;

public class AuthService : IAuthService
{
    private readonly string _instanceToken;
    private string? _bootstrapCode;
    private DateTime? _bootstrapCodeExpiry;
    private bool _bootstrapCodeUsed;
    private readonly object _bootstrapLock = new();

    public AuthService()
    {
        // Generate one random token per app instance (64 bytes = 512 bits)
        _instanceToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    }

    public string GetInstanceToken() => _instanceToken;

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        // Use constant-time comparison to prevent timing attacks
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token),
            System.Text.Encoding.UTF8.GetBytes(_instanceToken)
        );
    }

    public string GenerateBootstrapCode()
    {
        lock (_bootstrapLock)
        {
            // Generate a new one-time bootstrap code (32 bytes = 256 bits)
            _bootstrapCode = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").Replace("=", ""); // URL-safe base64
            _bootstrapCodeExpiry = DateTime.UtcNow.AddMinutes(2);
            _bootstrapCodeUsed = false;
            return _bootstrapCode;
        }
    }

    public bool ValidateAndConsumeBootstrapCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;

        lock (_bootstrapLock)
        {
            // Check if code exists, hasn't expired, and hasn't been used
            if (_bootstrapCode == null || _bootstrapCodeExpiry == null || _bootstrapCodeUsed)
                return false;

            if (DateTime.UtcNow > _bootstrapCodeExpiry)
                return false;

            // Constant-time comparison to prevent timing attacks
            var isValid = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(code),
                System.Text.Encoding.UTF8.GetBytes(_bootstrapCode)
            );

            if (isValid)
            {
                // Mark as used (single-use)
                _bootstrapCodeUsed = true;
                return true;
            }

            return false;
        }
    }
}
