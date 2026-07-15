using TokenSaver.Pipeline;

namespace TokenSaver;

/// <summary>
/// Where the proxy sends a full before/after record of every tool_result it considered rewriting.
///
/// This is the deliberate exception to <see cref="ILlmProxyEventSink"/>'s "never carries bodies"
/// rule, and the exception is the whole point: the savings tally can tell you THAT 40% came off,
/// but only a raw capture can tell you whether what came off mattered. Captures exist to answer
/// "was this compression correct", which is not a question byte counts can answer.
///
/// Consequences the host implementation owns, not this interface:
///   • Captures contain verbatim tool output — file contents, command output, paths. That is source
///     material from the user's machine. It is as sensitive as SessionLogs and must be treated the
///     same way (never checked in, never shipped off-box without the debug-bundle's encryption).
///   • Implementations MUST return immediately and MUST NOT throw. This is called on the LLM hot
///     path. A lost capture is a bad afternoon; a blocked relay is a broken product.
/// </summary>
public interface ICompressionCaptureSink
{
    /// <summary>Records one tool_result's compression. Fire-and-forget; never throws.</summary>
    void Capture(CompressionCapture capture);
}

/// <summary>
/// One tool_result's full compression record — the unit the compress runbook judges.
///
/// <paramref name="Id"/> is the handle: it is what you paste at an LLM reviewer, what the Vibe AI
/// capture view looks up, and what runbooks/compress_runbook.md is organized around. It is minted
/// per tool_result, not per request, because "this Bash output compressed wrong" is the actual
/// grain of every bug report — one request carries many tool_results and blaming the request tells
/// you nothing about which one broke.
///
/// <paramref name="RawText"/> and <paramref name="CompressedText"/> are the unescaped strings the
/// pipeline saw and produced, NOT wire bytes. That means they are directly re-runnable: feed
/// RawText back through a different <see cref="CompressionPlan"/> and you get an honest what-if.
/// This is what makes the Vibe AI preview real rather than a simulation.
///
/// <paramref name="RewriteAccepted"/> distinguishes a changed pipeline candidate from bytes that
/// were actually sent upstream. A candidate can be rejected when JSON re-serialization would make
/// its wire token larger; retaining that candidate is useful evidence, but calling it applied would
/// make the capture audit lie about the request.
/// </summary>
public sealed record CompressionCapture(
    Guid Id,
    string Provider,
    string ToolName,
    string? Command,
    string RawText,
    string CompressedText,
    IReadOnlyList<StageTrace> Trace,
    IReadOnlyList<string> EnabledIds,
    bool RewriteAccepted = false)
{
    public int CharsBefore => RawText.Length;
    public int CharsAfter => CompressedText.Length;

    /// <summary>True when the pipeline changed the string. Compares content, not length: a
    /// Reshaping stage can hold length constant while moving bytes.</summary>
    public bool Changed => !string.Equals(RawText, CompressedText, StringComparison.Ordinal);
}
