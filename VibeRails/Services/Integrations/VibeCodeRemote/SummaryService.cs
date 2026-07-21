using VibeRails.DTOs;
using VibeRails.Services.UserInOut;
using VibeRails.Utils;
using System.Text.RegularExpressions;

namespace VibeRails.Services.Integrations.VibeCodeRemote
{

    public interface ISummaryService
    {
        Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken);
    }

    public class SummaryService : ISummaryService
    {
        private readonly HttpClient _httpClient;
        public SummaryService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }
        public async Task<string> GetSummaryAsync(string transcripts, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(transcripts);

            if (!ParserConfigs.GetRemoteAccess())
            {
                throw new InvalidOperationException(
                    "Remote summaries are disabled. Enable Remote Access before sending a session transcript.");
            }

            var apiKey = ParserConfigs.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "Remote summaries require a configured VibeRails API key.");
            }

            var redactedTranscripts = RemoteTranscriptRedactor.Redact(transcripts, apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/summary")
            {
                Content = JsonContent.Create(
                    new SummaryPostDto { SessionText = redactedTranscripts },
                    AppJsonSerializerContext.Default.SummaryPostDto)
            };
            request.Headers.Add("X-Api-Key", apiKey);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.SummaryResponseDto, cancellationToken);
            return result?.Summary ?? "";
        }
    }

    /// <summary>
    /// Removes high-confidence credentials before a locally stored transcript crosses the
    /// process boundary. A matching line is removed in full: retaining surrounding text is not
    /// worth the risk that a provider-specific credential format leaves a secret fragment behind.
    /// </summary>
    internal static class RemoteTranscriptRedactor
    {
        internal const string RedactionMarker = "[REDACTED: possible credential]";

        private static readonly Regex AdditionalSecretPattern = new(
            """(?:https?://[^\s/@:]+:[^\s/@]+@|\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|["']?(?:authorization|proxy[-_]?authorization|cookie|set[-_]?cookie|password|passphrase|api[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret|private[-_]?key|aws[-_]?secret[-_]?access[-_]?key|_authToken)["']?\s*[:=]\s*["']?(?:(?:bearer|basic)\s+)?[^\s"',;]{8,}|\bAIza[A-Za-z0-9_-]{20,}|\b(?:sk|pk)_(?:live|test)_[A-Za-z0-9]{16,})""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));

        internal static string Redact(string transcript, string configuredApiKey)
        {
            var normalized = transcript.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var redactedLines = new List<string>(lines.Length);
            var insidePrivateKey = false;

            foreach (var line in lines)
            {
                if (!insidePrivateKey && IsPrivateKeyBegin(line))
                {
                    insidePrivateKey = !IsPrivateKeyEnd(line);
                    redactedLines.Add(RedactionMarker);
                    continue;
                }

                if (insidePrivateKey)
                {
                    if (IsPrivateKeyEnd(line))
                        insidePrivateKey = false;
                    continue;
                }

                redactedLines.Add(IsSensitive(line, configuredApiKey) ? RedactionMarker : line);
            }

            return string.Join('\n', redactedLines);
        }

        private static bool IsSensitive(string line, string configuredApiKey) =>
            line.Contains(configuredApiKey, StringComparison.Ordinal)
            || InputEtlFilter.ContainsSecret(line)
            || AdditionalSecretPattern.IsMatch(line);

        private static bool IsPrivateKeyBegin(string line) =>
            line.Contains("-----BEGIN ", StringComparison.OrdinalIgnoreCase)
            && line.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase);

        private static bool IsPrivateKeyEnd(string line) =>
            line.Contains("-----END ", StringComparison.OrdinalIgnoreCase)
            && line.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
    }

}
