namespace TokenSaver.Minify;

/// <summary>One proxied request body's rewrite outcome. Byte counts are wire bytes.</summary>
public sealed record ToolOutputRewriteResult(
    bool Rewritten,
    int BytesBefore,
    int BytesAfter,
    int ToolResultsMinified,
    int ToolResultsSeen,
    MinifyStats Transforms,
    CondenseStats Condensed = default)
{
    public int BytesSaved => BytesBefore - BytesAfter;
}
