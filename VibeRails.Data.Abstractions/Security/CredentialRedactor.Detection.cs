using System.Text.RegularExpressions;

namespace VibeRails.Security;

public static partial class CredentialRedactor
{
    // --- Secret detection patterns (compiled once) ---

    private static readonly Regex[] SecretPatterns =
    [
        // Anthropic API keys
        new(@"sk-ant-api\d{2}-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled),

        // OpenAI API keys
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled),

        // AWS access key IDs
        new(@"AKIA[A-Z0-9]{16}", RegexOptions.Compiled),

        // GitHub tokens (classic and fine-grained)
        new(@"gh[pos]_[A-Za-z0-9]{36,}", RegexOptions.Compiled),
        new(@"github_pat_[A-Za-z0-9_]{22,}", RegexOptions.Compiled),

        // Slack tokens
        new(@"xox[bpsar]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled),

        // Environment variable assignments setting secret-named variables. Leading
        // word boundary keeps the `setx?` branch from matching the "set" tail inside
        // ordinary words like "reset" — without it any sentence containing both
        // "reset" and a secret-named keyword (e.g. "How do I reset my password?")
        // gets falsely flagged.
        new(@"\b(?:export|setx?|\[Environment\].*SetEnvironmentVariable)\b.*\b(?:KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // PowerShell SetEnvironmentVariable with quoted args (catches the specific pattern from the user's example)
        new(@"\[Environment\]::SetEnvironmentVariable\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Inline credential assignments: password=xyz, token: abc123etc
        new(@"(?:password|token|secret|api_?key|credential|auth)\s*[=:]\s*\S{8,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    /// <summary>
    /// Detects known token formats using the existing search-index policy.
    /// Use RedactTranscript for outbound text, which also removes short explicit values.
    /// </summary>
    public static bool ContainsKnownSecret(string text)
    {
        foreach (var pattern in SecretPatterns)
        {
            if (pattern.IsMatch(text))
                return true;
        }
        return false;
    }
}
