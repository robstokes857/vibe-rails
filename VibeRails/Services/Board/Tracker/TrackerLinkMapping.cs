namespace VibeRails.Services.Board.Tracker;

/// <summary>
/// The link row's <c>Mapping</c>, <c>points:{field};status:{name}</c>: the points field and remote
/// status an item was imported with. Shared by Jira and Azure DevOps. Since VIBE-236 a pull writes
/// it once, with the card, and never reads it (see <see cref="TrackerImport"/>). It is still written
/// because an older VibeRails sharing the database skips an unchanged item only when its mapping
/// matches; without it that version would re-apply the item's fields over local edits.
/// </summary>
public static class TrackerLinkMapping
{
    private const string PointsPrefix = "points:";
    private const string StatusPrefix = "status:";

    public static string Serialize(string? pointsField, string status) =>
        (string.IsNullOrEmpty(pointsField) ? string.Empty : PointsPrefix + pointsField + ";") + StatusPrefix + status;
}
