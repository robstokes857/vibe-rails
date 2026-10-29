namespace VibeRails.Services.Board.Tracker;

/// <summary>Where a remote item stands with its board: all a pull needs to know about it.</summary>
public enum TrackerImportState
{
    /// <summary>No link yet: the pull copies the item into a new card.</summary>
    New,
    /// <summary>Linked to a live card, which the pull leaves exactly as it is.</summary>
    Imported,
    /// <summary>Linked to a card deleted in VibeRails, which stays deleted and is never recreated.</summary>
    Deleted
}

/// <summary>
/// The rule every tracker pull follows (VIBE-236). The tracker is the source of truth for its
/// items, and a pull copies each one into the board once, one way. Only a
/// <see cref="TrackerImportState.New"/> item is mapped, placed and created, with its link in the
/// same transaction. A linked card is never touched again, whatever later changes in the tracker
/// or on the card: no field update, no status note, no description repair, no move. The only
/// ongoing link is outbound: card comments and session links delivered to the item. Jira, Azure
/// DevOps and any later tracker share this; keep tracker-specific mapping in each stack.
/// </summary>
public static class TrackerImport
{
    /// <summary>
    /// Decided from the link before the item is read any further, so a dry run and a writing pull
    /// agree, an item already imported is never validated again, and no overflow lane is made for it.
    /// </summary>
    public static TrackerImportState Classify(bool linked, bool cardLive) =>
        !linked ? TrackerImportState.New
        : cardLive ? TrackerImportState.Imported
        : TrackerImportState.Deleted;
}

/// <summary>One pull's counts and its report message, worded the same for every tracker.</summary>
public sealed class TrackerPullCounts
{
    public int Created { get; private set; }
    public int AlreadyImported { get; private set; }
    /// <summary>Items left out: links whose card was deleted in VibeRails.</summary>
    public int Skipped { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Counts one item by how it ended: a <see cref="TrackerImportState.New"/> one was created (or, on a dry run, would be).</summary>
    public void Add(TrackerImportState state)
    {
        switch (state)
        {
            case TrackerImportState.New: Created++; break;
            case TrackerImportState.Imported: AlreadyImported++; break;
            default: Skipped++; break;
        }
    }

    public void AddFailed() => Failed++;

    public string Message(bool dryRun) =>
        (dryRun ? "Dry run. " : string.Empty)
        + $"{Created} created, {AlreadyImported} already imported, {Skipped} skipped, {Failed} failed."
        + (Skipped > 0 ? $" Skipped: {Skipped} previously deleted in VibeRails." : string.Empty);
}
