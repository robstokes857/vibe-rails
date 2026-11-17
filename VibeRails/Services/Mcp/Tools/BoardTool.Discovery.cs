using System.Text;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Tracker;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    private static bool SameProject(string left, string right) =>
        string.Equals(left, right, BoardPaths.ProjectPathComparison);

    private async Task AppendBoardsAsync(StringBuilder builder, string project, IReadOnlyList<BoardRecord> boards,
        string? currentBoardId, bool currentProject, IReadOnlyDictionary<string, string> providers, CancellationToken cancellationToken)
    {
        var columns = await store.GetAllColumnsAsync(project, cancellationToken);
        var counts = await store.CountCardsByBoardAsync(project, cancellationToken);
        var automations = await service.GetLaneAutomationsByLaneAsync(project,
            columns.Select(c => c.Id).ToList(), cancellationToken);
        var ordered = boards.OrderBy(b => b.Position).ThenBy(b => b.CreatedUtc).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
        currentBoardId ??= currentProject ? ordered.FirstOrDefault()?.Id : null;
        foreach (var board in ordered)
        {
            var count = counts.GetValueOrDefault(board.Id);
            builder.Append("- ").Append(board.Name).Append(" (id ").Append(board.Id).Append(", ")
                .Append(count).Append(" card").Append(count == 1 ? "" : "s");
            var lanes = columns.Where(c => c.BoardId == board.Id).OrderBy(c => c.Position).ToList();
            if (lanes.Count > 0)
            {
                // A lane's Agents and Actions read by name here, as in list_board_columns.
                var labels = new List<string>(lanes.Count);
                foreach (var lane in lanes)
                    labels.Add(LaneLabel(lane.Name, await NameLaneEntriesAsync(automations[lane.Id], launches, cancellationToken)));
                builder.Append("; lanes: ").Append(string.Join(" → ", labels));
            }
            // A dedicated tracker board names its provider; a board without one is local.
            if (providers.TryGetValue(board.Id, out var provider))
                builder.Append("; provider: ").Append(TrackerBoards.DisplayName(provider));
            if (currentProject && board.Id == currentBoardId) builder.Append("; current");
            builder.Append(")\n");
        }
    }
}
