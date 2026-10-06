using System.Buffers;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Board;

namespace VibeRails.Services.Jira;

/// <summary>One Jira board column as saved: <see cref="LaneId"/> is the user's choice, null for automatic.</summary>
public sealed record JiraColumnChoice(string Name, string? LaneId);

/// <summary>Where one Jira column's issues go. A null lane is the "Jira" overflow lane.</summary>
public sealed record JiraColumnLane(string Name, string? LaneId, string? LaneName, bool Automatic);

/// <summary>
/// Jira board column to VibeRails lane (VIBE-102). An issue's status id names its Jira column (the
/// board configuration lists each column's statuses), and the column names a lane. The saved map
/// (<c>BoardJiraConnections.ColumnMap</c>) keeps the board's column names in order plus any lane the
/// user picked; every other column is matched automatically on each pull, so lanes added or
/// renamed later are picked up.
/// </summary>
public static class JiraColumnMap
{
    public const int MaxColumns = 50;
    public const int MaxNameLength = 200;

    public static IReadOnlyList<JiraColumnChoice> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var columns = new List<JiraColumnChoice>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || name.GetString() is not { Length: > 0 } text)
                    continue;
                var lane = element.TryGetProperty("lane", out var laneElement) && laneElement.ValueKind == JsonValueKind.String
                    ? laneElement.GetString()
                    : null;
                columns.Add(new JiraColumnChoice(text, string.IsNullOrEmpty(lane) ? null : lane));
            }
            return columns;
        }
        catch (JsonException)
        {
            // A map this version can't read matches every column automatically.
            return [];
        }
    }

    public static string? Serialize(IReadOnlyList<JiraColumnChoice> columns)
    {
        if (columns.Count == 0)
            return null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var column in columns)
            {
                writer.WriteStartObject();
                writer.WriteString("name", column.Name);
                if (column.LaneId is null)
                    writer.WriteNull("lane");
                else
                    writer.WriteString("lane", column.LaneId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The board's current columns in order, keeping a lane picked for a column of the same name.</summary>
    public static IReadOnlyList<JiraColumnChoice> WithColumns(IReadOnlyList<JiraColumnChoice> saved, IEnumerable<string> names) =>
        names.Select(Clip)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxColumns)
            .Select(name => new JiraColumnChoice(name, Find(saved, name)?.LaneId))
            .ToList();

    /// <summary>
    /// Applies the user's picks: a lane id, or blank for automatic. A name the saved map doesn't
    /// have yet is appended, so a pick made before the first Connect is kept.
    /// </summary>
    public static IReadOnlyList<JiraColumnChoice> WithChoices(
        IReadOnlyList<JiraColumnChoice> saved, IReadOnlyDictionary<string, string?> choices)
    {
        var picks = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lane) in choices)
            picks.TryAdd(Clip(name), LaneIdOrNull(lane));
        var columns = saved.Select(column => picks.TryGetValue(column.Name, out var lane) ? column with { LaneId = lane } : column).ToList();
        foreach (var (name, lane) in picks)
        {
            if (name.Length == 0 || columns.Count >= MaxColumns || Find(columns, name) is not null)
                continue;
            columns.Add(new JiraColumnChoice(name, lane));
        }
        return columns;
    }

    // A lane id is only ever compared with this board's lane ids, so anything else reads as automatic.
    private static string? LaneIdOrNull(string? lane)
    {
        var text = lane?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > 100 ? null : text;
    }

    /// <summary>
    /// Each column's lane: the saved pick while that lane still exists on the board, otherwise
    /// <see cref="Automatic"/>. The overflow lane is never an automatic target.
    /// </summary>
    public static IReadOnlyList<JiraColumnLane> Resolve(
        IReadOnlyList<string> names, IReadOnlyList<JiraColumnChoice> saved,
        IReadOnlyList<BoardColumnRecord> lanes, string? overflowColumnId)
    {
        var candidates = lanes
            .Where(lane => lane.Id != overflowColumnId
                && !string.Equals(lane.Name, JiraFieldMapping.OverflowLaneName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var resolved = new List<JiraColumnLane>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var picked = Find(saved, names[i])?.LaneId is { } laneId ? lanes.FirstOrDefault(lane => lane.Id == laneId) : null;
            var lane = picked ?? Automatic(i, names, candidates);
            resolved.Add(new JiraColumnLane(names[i], lane?.Id, lane?.Name, picked is null));
        }
        return resolved;
    }

    /// <summary>
    /// The automatic lane for column <paramref name="index"/>: the lane with the same name; else
    /// the one lane whose name is a whole word of the column's ("In Review" goes to Review); else
    /// the first lane for the first column; else the Done lane (or the last lane) for the last
    /// column, which Jira treats as done. A middle column with no match goes to overflow (null).
    /// </summary>
    public static BoardColumnRecord? Automatic(int index, IReadOnlyList<string> names, IReadOnlyList<BoardColumnRecord> candidates)
    {
        if (candidates.Count == 0)
            return null;
        var name = names[index].Trim();
        var exact = candidates.Where(lane => string.Equals(lane.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return exact[0];
        if (exact.Count > 1)
            return null;

        var words = Words(name);
        var contained = candidates.Where(lane => Words(lane.Name) is { Length: > 1 } laneWords
            && (words.Contains(laneWords, StringComparison.Ordinal) || laneWords.Contains(words, StringComparison.Ordinal))).ToList();
        if (contained.Count == 1)
            return contained[0];

        if (index == 0)
            return candidates[0];
        if (index == names.Count - 1)
        {
            var done = candidates.Where(lane => string.Equals(lane.Name.Trim(), "Done", StringComparison.OrdinalIgnoreCase)).ToList();
            return done.Count == 1 ? done[0] : candidates[^1];
        }
        return null;
    }

    private static JiraColumnChoice? Find(IReadOnlyList<JiraColumnChoice> columns, string name) =>
        columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Clip(string? name)
    {
        var text = name?.Trim() ?? string.Empty;
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength];
    }

    // " in review " — lower-case letters and digits between single spaces, padded so a lane name
    // matches whole words only.
    private static string Words(string text)
    {
        var builder = new StringBuilder(" ");
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else if (builder[^1] != ' ')
                builder.Append(' ');
        }
        if (builder[^1] != ' ')
            builder.Append(' ');
        return builder.ToString();
    }
}
