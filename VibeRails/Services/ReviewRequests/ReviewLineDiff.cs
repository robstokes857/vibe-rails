namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Added and removed line counts between a file's base and staged text, for the card's staged
/// file list (slice E). Only the length of the shortest edit script is computed (Myers' greedy
/// forward pass over lines, O((N+M)·D)); with the counts of both sides that is enough:
/// <c>added − removed = M − N</c> and <c>added + removed = D</c>. The pass stops at
/// <see cref="MaxEdits"/> so one huge rewrite cannot stall the preview; the caller then shows the
/// snapshot's single changed-line count instead.
/// </summary>
public static class ReviewLineDiff
{
    /// <summary>The largest edit distance that is still counted; beyond it the answer is null.</summary>
    public const int MaxEdits = 20_000;

    /// <summary>
    /// Null when neither side has text or the edit is larger than <paramref name="maxEdits"/>.
    /// A side without text (an added or deleted file) counts every line of the other side.
    /// </summary>
    public static (int Added, int Removed)? Count(string? before, string? after, int maxEdits = MaxEdits)
    {
        if (before is null && after is null) return null;
        var a = Lines(before);
        var b = Lines(after);
        if (a.Length == 0) return (b.Length, 0);
        if (b.Length == 0) return (0, a.Length);

        // Common prefix and suffix never take part in the edit; trimming them keeps the pass short
        // for the usual case of a small change in a large file.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        var n = endA - start;
        var m = endB - start;
        if (n == 0) return (m, 0);
        if (m == 0) return (0, n);

        var distance = EditDistance(a, b, start, n, m, Math.Min(maxEdits, n + m));
        if (distance < 0) return null;
        var added = (distance + (m - n)) / 2;
        return (added, distance - added);
    }

    private static int EditDistance(string[] a, string[] b, int start, int n, int m, int maxEdits)
    {
        // V[k] is the furthest x reached on diagonal k after the previous step; k ranges -d..d, so
        // the array is offset by maxEdits + 1 and never indexed outside it while d <= maxEdits.
        var offset = maxEdits + 1;
        var v = new int[2 * maxEdits + 3];
        v[offset + 1] = 0;
        for (var d = 0; d <= maxEdits; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    x = v[offset + k + 1];
                else
                    x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[start + x] == b[start + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m) return d;
            }
        }
        return -1;
    }

    /// <summary>Lines split on <c>\n</c>; a trailing newline does not make an extra empty line, and <c>\r</c> stays with its line as git keeps it.</summary>
    internal static string[] Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
