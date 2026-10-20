using System.Text;

namespace VibeRails.Services.VCA;

/// <summary><c>Signed Approval('path', 'approver@example.com')</c> after parsing.</summary>
/// <param name="RelativePath">File or directory, relative to the declaring vc.rules.md.</param>
/// <param name="Approvers">Lowercased, deduplicated emails; any one of them satisfies the rule.</param>
public sealed record SignedApprovalSpecification(string RelativePath, IReadOnlyList<string> Approvers);

/// <summary>
/// Syntax and wording for the two rules a person satisfies by signing the commit in the Git Guard
/// popup with a Settings &gt; KEYS signing key:
/// <list type="bullet">
/// <item><c>Human verified</c>: every commit in scope needs a signature from any signing key
/// registered on viberails.ai to an account with a verified email.</item>
/// <item><c>Signed Approval('db.cs', 'owner@example.com')</c>: a commit that changes the path needs
/// a signature whose viberails.ai account email is one of the listed approvers.</item>
/// </list>
/// The signature covers the staged tree id, not the commit id. A commit id hashes the message, and
/// the message is where the signature lives, so a commit cannot carry a signature over its own id.
/// The tree id is the hash of exactly the content being committed, so any change after signing
/// invalidates the signature. See <see cref="CommitSignatureFormat"/>.
/// </summary>
public static class HumanSignatureRule
{
    public const string HumanVerifiedName = "Human verified";
    public const string SignedApprovalTemplate = "Signed Approval('path to file or directory', 'approver@example.com')";
    public const int MaxApprovers = 10;

    private const string SignedApprovalPrefix = "Signed Approval";

    public const string SignedApprovalSyntaxHelp =
        "Use Signed Approval('path/to/file', 'approver@example.com'). The path is relative to the declaring "
        + "vc.rules.md and may name a file or a directory; separate several approvers with commas inside "
        + "the second quotes.";

    public const string Guidance =
        "A person signs the commit in the Git Guard popup, which opens when commit-msg runs on Windows: "
        + "choose a Settings > KEYS signing key synced to viberails.ai and type its password. The popup "
        + "adds a vibe-signature: line to the commit message. Agents cannot create this signature, and "
        + "Git Guard does not open the popup in CI.";

    public const string HumanVerifiedDescription =
        "Requires a person to approve each commit by signing it in the Git Guard popup with a Settings > KEYS "
        + "signing key registered on viberails.ai. Git Guard verifies the signature with viberails.ai at "
        + "commit-msg. The signature covers the exact staged content, so changing anything after signing "
        + "needs a new signature. Use STOP: at COMMIT an acknowledgment can replace the signature. "
        + "Removing or weakening this rule needs the same signature.";

    public const string SignedApprovalDescription =
        "When the file or directory changes, requires a signature from a listed approver. The approver signs "
        + "in the Git Guard popup with a Settings > KEYS key, and viberails.ai must report one of the listed "
        + "emails as the key's verified account email. Format: Signed Approval('path/to/file', 'approver@example.com'). "
        + "Use STOP: at COMMIT an acknowledgment can replace the approval.";

    public static string HumanVerifiedDeferredMessage =>
        "Deferred: a person must sign this commit; checked against the final commit message by commit-msg. "
        + Guidance;

    public static string SignedApprovalDeferredMessage(string path, IReadOnlyList<string> approvers) =>
        $"Deferred: changes to '{path}' need a signed approval from {DescribeApprovers(approvers)}; checked "
        + "against the final commit message by commit-msg. " + Guidance;

    /// <summary>Matches the catalog name, ignoring case and surrounding whitespace.</summary>
    public static bool IsHumanVerified(string? text) =>
        string.Equals(text?.Trim(), HumanVerifiedName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Text that is meant to be a signed approval, whether or not it parses.</summary>
    public static bool LooksLikeSignedApproval(string? text) =>
        text?.TrimStart().StartsWith(SignedApprovalPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Either rule a person satisfies through the signing popup.</summary>
    public static bool IsSignatureRule(string? text) => IsHumanVerified(text) || LooksLikeSignedApproval(text);

    public static string DescribeApprovers(IReadOnlyList<string> approvers) => approvers.Count switch
    {
        0 => "any registered signing key",
        1 => approvers[0],
        _ => "one of " + string.Join(", ", approvers)
    };

    /// <summary>
    /// Parses <c>Signed Approval('path', 'a@example.com[, b@example.com]')</c>. Either quote may be
    /// single or double; the path cannot contain its own quote character. A line break anywhere is
    /// refused for the same reason as path locks: the writer would split the rule into two lines.
    /// </summary>
    public static bool TryParseSignedApproval(string? ruleText, out SignedApprovalSpecification specification)
    {
        specification = new SignedApprovalSpecification(string.Empty, []);
        if (string.IsNullOrWhiteSpace(ruleText) || ruleText.AsSpan().ContainsAny('\r', '\n', '\0'))
            return false;

        var text = ruleText.Trim();
        if (!text.StartsWith(SignedApprovalPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = text[SignedApprovalPrefix.Length..].Trim();
        if (remainder.Length < 2 || remainder[0] != '(' || remainder[^1] != ')')
            return false;

        var arguments = remainder[1..^1];
        var position = 0;
        if (!TryReadQuoted(arguments, ref position, out var path)
            || !TrySkip(arguments, ref position, ',')
            || !TryReadQuoted(arguments, ref position, out var approverList))
            return false;
        if (arguments[position..].Trim().Length != 0)
            return false;

        path = path.Trim();
        if (path.Length == 0)
            return false;

        var approvers = new List<string>();
        foreach (var entry in approverList.Split(','))
        {
            var email = entry.Trim();
            if (!IsEmail(email))
                return false;
            email = email.ToLowerInvariant();
            if (!approvers.Contains(email, StringComparer.Ordinal))
                approvers.Add(email);
        }
        if (approvers.Count is 0 or > MaxApprovers)
            return false;

        specification = new SignedApprovalSpecification(path, approvers);
        return true;
    }

    /// <summary>Canonical text for a parsed approval, as the rule writers store it.</summary>
    public static string Format(SignedApprovalSpecification specification) =>
        $"{SignedApprovalPrefix}('{specification.RelativePath}', '{string.Join(", ", specification.Approvers)}')";

    /// <summary>
    /// A deliberately plain address check: one '@', a dotted domain, nothing that could end a quoted
    /// rule argument or be mistaken for a list separator. The approver is compared with the verified
    /// email viberails.ai returns, so exotic-but-valid forms gain nothing here.
    /// </summary>
    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 254) return false;
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at > 64) return false;
        var domain = value[(at + 1)..];
        if (domain.Length < 3 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.')
            || domain.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character)
                || character is '\'' or '"' or ',' or '(' or ')' or '<' or '>' or ';' or '\\' or '`')
                return false;
        }
        foreach (var character in domain)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
                return false;
        }
        return true;
    }

    private static bool TryReadQuoted(string text, ref int position, out string value)
    {
        value = string.Empty;
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position >= text.Length || text[position] is not ('\'' or '"')) return false;
        var quote = text[position];
        var end = text.IndexOf(quote, position + 1);
        if (end < 0) return false;
        value = text[(position + 1)..end];
        position = end + 1;
        return true;
    }

    private static bool TrySkip(string text, ref int position, char expected)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position >= text.Length || text[position] != expected) return false;
        position++;
        return true;
    }
}

/// <summary>One <c>vibe-signature:</c> line from a commit message.</summary>
/// <param name="KeyId">The key's viberails.ai id (not the local key file id).</param>
/// <param name="Fingerprint">SHA-256 of the key's SubjectPublicKeyInfo, 64 lowercase hex characters.</param>
/// <param name="TreeId">The staged tree the signature covers.</param>
/// <param name="SignatureBase64">RSA-PSS-SHA256 over <see cref="CommitSignatureFormat.BuildPayload"/>.</param>
public sealed record CommitSignature(Guid KeyId, string Fingerprint, string TreeId, string SignatureBase64);

/// <summary>
/// The signed payload and the commit-message line that carries a signature.
/// <para>
/// Payload: <c>"VibeRails human-verified commit v1\ntree &lt;tree id&gt;\n"</c>. The prefix is domain
/// separation: the dashboard's general Sign message route refuses any payload that starts with it, and
/// no MCP tool signs anything, so the Git Guard popup is the only VibeRails path that produces one.
/// </para>
/// <para>
/// Line: <c>vibe-signature: v1 key=&lt;guid&gt; fp=&lt;hex&gt; tree=&lt;hex&gt; sig=&lt;base64&gt;</c>. The tree is
/// repeated in the line so a reader can see what was signed, and so a stale signature produces a
/// precise "content changed after signing" message instead of a bare verification failure.
/// </para>
/// </summary>
public static class CommitSignatureFormat
{
    public const string Trailer = "vibe-signature:";
    public const string Version = "v1";
    public const string Algorithm = "RSA-PSS-SHA256";
    public const string PayloadPrefix = "VibeRails human-verified commit v1\n";

    /// <summary>Bounds the network work one commit message can cause.</summary>
    public const int MaxSignaturesPerMessage = 5;

    public static byte[] BuildPayload(string treeId) =>
        Encoding.UTF8.GetBytes($"{PayloadPrefix}tree {treeId}\n");

    /// <summary>A full SHA-1 or SHA-256 object id in lowercase hex, as git prints it.</summary>
    public static bool IsObjectId(string? value) => value is { Length: 40 or 64 } && IsLowerHex(value);

    public static bool IsFingerprint(string? value) => value is { Length: 64 } && IsLowerHex(value);

    public static string Format(CommitSignature signature) =>
        $"{Trailer} {Version} key={signature.KeyId:D} fp={signature.Fingerprint} tree={signature.TreeId} sig={signature.SignatureBase64}";

    /// <summary>
    /// Every well-formed signature line in a message whose Git comments are already stripped.
    /// Malformed lines are ignored rather than reported: they cannot satisfy the rule, and the rule's
    /// own message already says what is missing. Duplicates collapse and at most
    /// <see cref="MaxSignaturesPerMessage"/> are returned.
    /// </summary>
    public static IReadOnlyList<CommitSignature> Parse(string? message)
    {
        var signatures = new List<CommitSignature>();
        if (string.IsNullOrWhiteSpace(message)) return signatures;

        using var lines = new StringReader(message);
        while (lines.ReadLine() is { } line && signatures.Count < MaxSignaturesPerMessage)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(Trailer, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryParseLine(trimmed[Trailer.Length..], out var signature)
                && !signatures.Any(existing => existing.SignatureBase64 == signature.SignatureBase64))
                signatures.Add(signature);
        }
        return signatures;
    }

    private static bool TryParseLine(string body, out CommitSignature signature)
    {
        signature = null!;
        var tokens = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 5 || !tokens[0].Equals(Version, StringComparison.OrdinalIgnoreCase)) return false;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.Skip(1))
        {
            var equals = token.IndexOf('=');
            if (equals <= 0) return false;
            var name = token[..equals].ToLowerInvariant();
            if (name is not ("key" or "fp" or "tree" or "sig") || !fields.TryAdd(name, token[(equals + 1)..]))
                return false;
        }

        if (!Guid.TryParseExact(fields["key"], "D", out var keyId) || keyId == Guid.Empty) return false;
        var fingerprint = fields["fp"].ToLowerInvariant();
        var tree = fields["tree"].ToLowerInvariant();
        var signatureText = fields["sig"];
        if (!IsFingerprint(fingerprint) || !IsObjectId(tree) || signatureText.Length > 684) return false;

        try
        {
            var bytes = Convert.FromBase64String(signatureText);
            // RSA-2048 through RSA-4096; the server applies the same bounds.
            if (bytes.Length is < 256 or > 512) return false;
        }
        catch (FormatException) { return false; }

        signature = new CommitSignature(keyId, fingerprint, tree, signatureText);
        return true;
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }
}
