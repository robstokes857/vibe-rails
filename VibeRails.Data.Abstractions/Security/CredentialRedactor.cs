using System.Text;
using System.Text.RegularExpressions;

namespace VibeRails.Security;

/// <summary>
/// Shared credential detection and redaction for transcripts, configuration exports and search.
/// Each operation retains its context's policy: transcripts get best-effort value masking,
/// configuration values retain their surrounding shape, and search uses its existing detector.
/// All operations are pure and independent of application settings.
/// </summary>
public static partial class CredentialRedactor
{
    /// <summary>Replaces each credential value found in a transcript.</summary>
    public const string TranscriptMask = "XXXXX";

    // Best effort, on purpose (see API_SEC.md). Someone asking why PBKDF2 beats Blowfish is far
    // more likely than someone pasting a password, so mask the obvious shapes (key=value fields,
    // CLI flags, auth headers, URL user-info, known token formats, PEM keys) and keep the rest of
    // the transcript intact. A random password in prose cannot be recognized and is not attempted.
    // Every pattern starts on a literal and has no nested quantifier, so a pass stays linear on
    // multi-megabyte transcripts.
    private const string FieldNames = @"passw(?:or)?d|pwd|passphrase|secret|token|api[-_]?key|access[-_]?key|private[-_]?key|credentials?";
    private const string FlagNames = @"passw(?:or)?d|pwd|passphrase|secret|client[-_]?secret|token|access[-_]?token|auth[-_]?token|refresh[-_]?token|api[-_]?key";
    private const string Separator = @"(?:\\?[""'])?[\]})]?[ \t]*(?::=|[:=])[ \t]*";
    private const string QuotedValue = @"\\?[""'])(?<value>[^""'\\\r\n]+)";
    private const string BareValue = @")(?<value>[^\s""'\\`,;&<>()\[\]{}=$%][^\s""'\\`,;&<>()\[\]{}]*)";

    [GeneratedRegex(
        // password = "value", "api_key": 'value', DB_PASSWORD=value, Pwd=value; (connection strings)
        "(?<keep>(?:" + FieldNames + ")" + Separator + QuotedValue
        + "|(?<keep>(?:" + FieldNames + ")" + Separator + BareValue
        // --password value, -Password "value"
        + @"|(?<keep>(?<![\w-])--?(?:" + FlagNames + @")[ \t]+" + QuotedValue
        + @"|(?<keep>(?<![\w-])--?(?:" + FlagNames + @")[ \t]+)(?<value>[^\s""'\\-][^\s""'\\]*)"
        // SetEnvironmentVariable("OPENAI_API_KEY", "value")
        + "|(?<keep>(?:" + FieldNames + @")\\?[""'][ \t]*,[ \t]*" + QuotedValue
        // Authorization: Bearer value (also Proxy-Authorization)
        + @"|(?<keep>authorization(?:\\?[""'])?[ \t]*[:=][ \t]*(?:\\?[""'])?(?:(?:bearer|basic|token|digest|bot|apikey)[ \t]+)?)(?<value>[^\s""'\\,;]+)"
        // A bare "Bearer <token>" needs a long token with a digit (checked in MaskField), so "Bearer authentication" stays.
        + @"|(?<keep>\bbearer[ \t]+)(?<bearer>[A-Za-z0-9._~+/-]{16,}=*)"
        + @"|(?<keep>\b(?:set-)?cookie[ \t]*:[ \t]*)(?<value>[^\r\n]+)"
        // scheme://user:password@host
        + @"|(?<keep>:(?:\\?/){2}[^\s/:@""'<>]*:)(?<value>[^\s/@""'<>]+)(?<tail>@)"
        // <password>value</password>
        + @"|(?<keep><[\w.:-]*(?:" + FieldNames + @")>)(?<value>[^<\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex CredentialFieldPattern();

    [GeneratedRegex(
        @"\bsk-[A-Za-z0-9_-]{20,}"
        + @"|\b(?:sk|pk|rk)_(?:live|test)_[A-Za-z0-9]{16,}"
        + @"|\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"
        + @"|\bgh[pousr]_[A-Za-z0-9]{30,}"
        + @"|\bgithub_pat_[A-Za-z0-9_]{22,}"
        + @"|\bglpat-[A-Za-z0-9_-]{20,}"
        + @"|\bxox[abposr]-[A-Za-z0-9-]{10,}"
        + @"|\bAIza[0-9A-Za-z_-]{30,}"
        + @"|\bya29\.[0-9A-Za-z_-]{20,}"
        + @"|\bnpm_[A-Za-z0-9]{36}\b"
        + @"|\bhf_[A-Za-z0-9]{30,}"
        + @"|\bxai-[A-Za-z0-9]{30,}"
        + @"|\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPattern();

    // Values that describe a field rather than hold one: "password: string", "token: null".
    private static readonly HashSet<string> NonSecretValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "str", "null", "none", "nil", "undefined", "true", "false", "bool", "boolean",
        "int", "number", "any", "object", "required", "optional", TranscriptMask,
    };

    /// <summary>
    /// Best-effort masking of credential values in free-form text: each recognized value becomes
    /// <see cref="TranscriptMask"/> and everything else, including line structure, is kept.
    /// Optionally masks a known secret wherever it appears. Does not read settings or log text.
    /// </summary>
    public static string RedactTranscript(string transcript, string? knownSecret = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var text = transcript;
        if (!string.IsNullOrEmpty(knownSecret))
            text = text.Replace(knownSecret, TranscriptMask, StringComparison.Ordinal);
        text = MaskPrivateKeys(text);
        text = KnownTokenPattern().Replace(text, TranscriptMask);
        return CredentialFieldPattern().Replace(text, match => MaskField(text, match));
    }

    private static string MaskField(string text, Match match)
    {
        var keep = match.Groups["keep"].Value;
        var bearer = match.Groups["bearer"];
        if (bearer.Success)
            return bearer.ValueSpan.IndexOfAnyInRange('0', '9') >= 0 ? keep + TranscriptMask : match.Value;

        var end = match.Index + match.Length;
        if (NonSecretValues.Contains(match.Groups["value"].Value)
            || (end < text.Length && text[end] is '(' or '[')   // token = GetToken(...), key: Optional[str]
            || IsCancellationToken(text, match.Index, keep))     // cancellationToken: ct
            return match.Value;
        return keep + TranscriptMask + match.Groups["tail"].Value;
    }

    private static bool IsCancellationToken(string text, int index, string keep)
    {
        if (!keep.StartsWith("token", StringComparison.OrdinalIgnoreCase)) return false;
        var start = index;
        while (start > 0 && char.IsAsciiLetter(text[start - 1])) start--;
        var prefix = text.AsSpan(start, index - start);
        return prefix.EndsWith("cancellation", StringComparison.OrdinalIgnoreCase)
            || prefix.EndsWith("stopping", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces each PEM private key block, header to footer, with one mask.</summary>
    private static string MaskPrivateKeys(string text)
    {
        const string begin = "-----BEGIN ";
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0) return text;
        var output = new StringBuilder(text.Length);
        var copied = 0;
        while (start >= 0)
        {
            // A header is one short line: -----BEGIN [RSA |OPENSSH |ENCRYPTED ]PRIVATE KEY-----
            var label = Window(text, start + begin.Length, 64);
            var close = label.IndexOf("-----", StringComparison.Ordinal);
            if (close < 0 || label[..close].ContainsAny('\r', '\n')
                || !label[..close].Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            {
                start = text.IndexOf(begin, start + begin.Length, StringComparison.Ordinal);
                continue;
            }
            var body = start + begin.Length + close + 5;
            var stop = body < text.Length && text[body] == '\\' ? EndOfEscapedKey(text, body) : EndOfKeyBody(text, body);
            output.Append(text, copied, start - copied).Append(TranscriptMask);
            copied = stop;
            start = text.IndexOf(begin, stop, StringComparison.Ordinal);
        }
        return output.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// The end of the footer after the base64 body lines. Without a footer, the body ends at the
    /// first other line, and the line break before that line is kept.
    /// </summary>
    private static int EndOfKeyBody(string text, int position)
    {
        while (position < text.Length)
        {
            var line = Window(text, position, 1024);
            var newline = line.IndexOf('\n');
            if (newline >= 0) line = line[..(newline + 1)];
            var content = line.Trim();
            var close = content.StartsWith("-----END ", StringComparison.Ordinal)
                ? content[9..].IndexOf("-----", StringComparison.Ordinal) : -1;
            if (close >= 0) return position + (line.Length - line.TrimStart().Length) + 9 + close + 5;
            if (content.ContainsAnyExcept(Base64Characters))
            {
                if (position > 0 && text[position - 1] == '\n') position--;
                if (position > 0 && text[position - 1] == '\r') position--;
                return position;
            }
            position += line.Length;
        }
        return text.Length;
    }

    /// <summary>
    /// A key inside a JSON or code string ("-----BEGIN PRIVATE KEY-----\nMIIE...") sits on one
    /// physical line: mask through its escaped footer, else up to the closing quote or line end.
    /// </summary>
    private static int EndOfEscapedKey(string text, int position)
    {
        var line = Window(text, position, 16384);
        var newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0) line = line[..newline];
        var footer = line.IndexOf("-----END ", StringComparison.Ordinal);
        var close = footer < 0 ? -1 : line[(footer + 9)..].IndexOf("-----", StringComparison.Ordinal);
        if (close >= 0) return position + footer + 9 + close + 5;
        var quote = line.IndexOfAny('"', '\'');
        return position + (quote >= 0 ? quote : line.Length);
    }

    private static ReadOnlySpan<char> Window(string text, int start, int length) =>
        text.AsSpan(start, Math.Min(length, text.Length - start));

    private static readonly System.Buffers.SearchValues<char> Base64Characters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");
}
