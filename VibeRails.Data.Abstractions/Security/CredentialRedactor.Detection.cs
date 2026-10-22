using System.Text.RegularExpressions;

namespace VibeRails.Security;

public static partial class CredentialRedactor
{
    // Detection is used as an exclusion filter. Fail closed on over-budget text/timeouts so
    // a secret beyond the examined window cannot enter the index or a search response.
    private const int SecretLineWindow = 4096;
    private const int SecretTextLimit = 1024 * 1024;
    private static readonly TimeSpan SecretMatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex[] SecretPatterns =
    [
        // Anthropic API keys
        new(@"sk-ant-api\d{2}-[A-Za-z0-9_-]{20,}", RegexOptions.NonBacktracking, SecretMatchTimeout),

        // OpenAI API keys
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.NonBacktracking, SecretMatchTimeout),

        // AWS access key IDs
        new(@"AKIA[A-Z0-9]{16}", RegexOptions.NonBacktracking, SecretMatchTimeout),

        // GitHub tokens (classic and fine-grained)
        new(@"gh[pos]_[A-Za-z0-9]{36,}", RegexOptions.NonBacktracking, SecretMatchTimeout),
        new(@"github_pat_[A-Za-z0-9_]{22,}", RegexOptions.NonBacktracking, SecretMatchTimeout),

        // Slack tokens
        new(@"xox[bpsar]-[A-Za-z0-9\-]{10,}", RegexOptions.NonBacktracking, SecretMatchTimeout),

        // Environment variable assignments setting secret-named variables. Leading
        // word boundary keeps the `setx?` branch from matching the "set" tail inside
        // ordinary words like "reset" — without it any sentence containing both
        // "reset" and a secret-named keyword (e.g. "How do I reset my password?")
        // gets falsely flagged.
        new(@"\b(?:export|setx?|\[Environment\].*SetEnvironmentVariable)\b.*\b(?:KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL)\b",
            RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, SecretMatchTimeout),

        // PowerShell SetEnvironmentVariable with quoted args (catches the specific pattern from the user's example)
        new(@"\[Environment\]::SetEnvironmentVariable\s*\(",
            RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, SecretMatchTimeout),

        // Inline credential assignments: password=xyz, token: abc123etc
        new(@"(?:password|token|secret|api_?key|credential|auth)\s*[=:]\s*\S{8,}",
            RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, SecretMatchTimeout),
    ];

    /// <summary>
    /// Detects known token formats using the existing search-index policy.
    /// Use RedactTranscript for outbound text, which also removes short explicit values.
    /// </summary>
    public static bool ContainsKnownSecret(string text)
    {
        if (text.Length > SecretTextLimit) return true;
        try
        {
            foreach (var line in text.AsSpan().EnumerateLines())
            {
                if (line.Length > SecretLineWindow) return true;
                foreach (var pattern in SecretPatterns)
                    if (pattern.IsMatch(line)) return true;
            }
        }
        catch (RegexMatchTimeoutException) { return true; }
        return false;
    }
}
