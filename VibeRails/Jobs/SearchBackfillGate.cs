using VibeRails.Services;

namespace VibeRails.Jobs;

/// <summary>What <see cref="SearchBackfillGate.Decide"/> allows: run inference now (and why), or hold.</summary>
internal enum SearchBackfillDecision
{
    /// <summary>A few new items, such as a card, a comment or some captures: index them right away.</summary>
    SmallBacklog,
    /// <summary>A large backlog and an idle machine.</summary>
    Idle,
    /// <summary>A large backlog that has waited <see cref="SearchBackfillGate.MaxWait"/>: run even though the machine is busy.</summary>
    WaitCapped,
    /// <summary>A large backlog on a busy machine: wait.</summary>
    Hold,
}

/// <summary>
/// Decides when <see cref="BertEmbeddingBackfillJob"/> may run inference (VIBE-172). A small backlog always
/// runs, so search finds new work right away. A large one waits for an idle machine. That means a first
/// start or a <c>SearchIndexVersions</c> model or chunk bump: about 18K chunks and 80–100 minutes of
/// batches. It runs anyway once it has waited <see cref="MaxWait"/>, so search cannot starve on a box
/// that is always busy.
/// </summary>
internal sealed class SearchBackfillGate
{
    /// <summary>Work items (sources plus chunks) that still count as fresh work: a few minutes of batches.</summary>
    internal const int SmallBacklog = 500;
    /// <summary>How long the rest of the machine must stay under the quiet CPU threshold.</summary>
    internal static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(2);
    /// <summary>How long without keyboard or mouse input, where the platform reports it (Windows).</summary>
    internal static readonly TimeSpan UserAwayWindow = TimeSpan.FromMinutes(2);
    /// <summary>The longest a large backlog waits for an idle machine, counted from when it was first seen.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    /// <summary>When the current large backlog was first seen; null while the backlog is small.</summary>
    public DateTimeOffset? LargeSince { get; private set; }

    public SearchBackfillDecision Decide(int pending, ResourceSnapshot machine, DateTimeOffset now)
    {
        if (pending <= SmallBacklog)
        {
            LargeSince = null;
            return SearchBackfillDecision.SmallBacklog;
        }
        LargeSince ??= now;
        if (IsIdle(machine)) return SearchBackfillDecision.Idle;
        return now - LargeSince.Value >= MaxWait ? SearchBackfillDecision.WaitCapped : SearchBackfillDecision.Hold;
    }

    /// <summary>
    /// Everything except this process has used little CPU for <see cref="QuietWindow"/> and, where the
    /// platform reports input, nobody has touched the keyboard or mouse for <see cref="UserAwayWindow"/>.
    /// </summary>
    internal static bool IsIdle(ResourceSnapshot machine) =>
        machine.QuietFor >= QuietWindow && (machine.UserIdleFor is not { } away || away >= UserAwayWindow);
}
