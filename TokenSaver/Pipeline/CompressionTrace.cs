namespace TokenSaver.Pipeline;

/// <summary>Why a stage did or didn't change anything. The vocabulary a human debugging a capture
/// reads first, so each value must answer a different question.</summary>
public enum StageOutcome
{
    /// <summary>Ran and removed chars.</summary>
    Applied,

    /// <summary>Ran, found nothing to do. The stage is working; this input had nothing for it.</summary>
    NoChange,

    /// <summary>Turned off in settings. Nothing was attempted.</summary>
    Disabled,

    /// <summary>On, but this input isn't its shape (a git-status grouper looking at `npm test`).</summary>
    NotApplicable,

    /// <summary>Bailed to protect the input — a malformed escape sequence or a control char that
    /// makes the transform's idempotency proof void. See OutputMinifier's class remarks; this is a
    /// designed outcome, not an error.</summary>
    Aborted,
}

/// <summary>
/// One stage's contribution to one tool_result.
///
/// <paramref name="CharsRemoved"/> is char-domain, not wire bytes: one stripped ESC is 6 wire bytes
/// after JSON escaping while one stripped space is 1. Use it to compare stages against each other,
/// never to quote a savings number — the honest wire figure is the rewriter's byte count. This is
/// the same distinction MinifyStats documents, and it is worth keeping straight when judging a
/// capture.
/// </summary>
public sealed record StageTrace(string StageId, StageOutcome Outcome, int CharsRemoved = 0);
