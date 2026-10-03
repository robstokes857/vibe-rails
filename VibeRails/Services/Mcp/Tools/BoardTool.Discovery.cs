using System.Text;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    private static bool SameProject(string left, string right) =>
        string.Equals(left, right, BoardPaths.ProjectPathComparison);

    private async Task AppendBoardsAsync(StringBuilder builder, string project, IReadOnlyList<BoardRecord> boards,
        string? currentBoardId, bool currentProject, CancellationToken cancellationToken)
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
                builder.Append("; lanes: ").Append(string.Join(" → ", lanes.Select(c => LaneLabel(c.Name, automations[c.Id]))));
            if (currentProject && board.Id == currentBoardId) builder.Append("; current");
            builder.Append(")\n");
        }
    }
}
