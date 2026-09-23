using System.Text.RegularExpressions;

namespace VibeRails.Services.Board;

/// <summary>
/// Finds the <c>@path</c> file references in card text (VB-35). The text is the only source of
/// truth — there is no linked-files table — so this mirrors the renderer's rule in
/// <c>board-text.js</c>; keep the two in step. A reference starts the text or follows whitespace
/// (an email or "@claude" mid-sentence is prose), and is either <c>@"a quoted path"</c> or a bare
/// run of non-space characters that contains a slash or a dot so it looks like a path. Trailing
/// sentence punctuation stays outside a bare reference, as it does for URLs, and nothing inside
/// inline code or a fenced block counts.
/// </summary>
public static partial class BoardFileReferences
{
    /// <summary>Longer than any sane repository path; a runaway token is dropped rather than carried into a prompt.</summary>
    public const int MaxReferenceLength = 512;

    /// <summary>Distinct references in first-seen order, exactly as typed (no normalisation, no existence check).</summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Inline code wins over a reference, and so does a fence: blank both out before matching.
        var prose = InlineCodePattern().Replace(FencePattern().Replace(text, " "), " ");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<string>();
        foreach (Match match in ReferencePattern().Matches(prose))
        {
            var quoted = match.Groups["quoted"];
            var path = quoted.Success ? quoted.Value : match.Groups["bare"].Value;
            if (!quoted.Success && !path.AsSpan().ContainsAny('/', '.'))
                continue; // "@claude", "@rob": no slash, no extension, not a file
            if (path.Length > MaxReferenceLength)
                continue;
            if (seen.Add(path))
                references.Add(path);
        }
        return references;
    }

    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"`[^`\n]+`")]
    private static partial Regex InlineCodePattern();

    // `&` is excluded from the bare run for parity with the renderer, which matches on escaped
    // text where every entity starts with `&`; a path containing `&` uses the quoted form.
    [GeneratedRegex(@"(?<![^\s])@(?:""(?<quoted>[^""\n]+)""|(?<bare>[^\s<>""'`&]*[^\s<>""'`&.,;:!?)\]}]))")]
    private static partial Regex ReferencePattern();
}
