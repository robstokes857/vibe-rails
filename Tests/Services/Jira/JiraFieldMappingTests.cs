using VibeRails.Services.Board;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraFieldMappingTests
{
    [Fact]
    public void MapsTheDocumentedFieldsAndLeavesAChoreAlone()
    {
        var issue = Issue("Bug hunt", "Bug", "Highest", ["one", "  two  "], 5);

        var mapped = JiraFieldMapping.Map(issue, "customfield_10016", BoardCardTypes.Task);
        Assert.Equal("Bug hunt", mapped.Title);
        Assert.Equal(BoardCardTypes.Bug, mapped.Type);
        Assert.Equal("critical", mapped.Priority);
        Assert.Equal(5, mapped.Points);
        Assert.Equal(["one", "two"], mapped.Tags);

        var chore = JiraFieldMapping.Map(issue, "customfield_10016", BoardCardTypes.Chore);
        Assert.Equal(BoardCardTypes.Chore, chore.Type);
    }

    [Theory]
    [InlineData("Story", "feature")]
    [InlineData("New Feature", "feature")]
    [InlineData("Spike", "research-spike")]
    [InlineData("Sub-task", "task")]
    [InlineData("Epic", "task")]
    public void MapsIssueTypes(string issueType, string expected) =>
        Assert.Equal(expected, JiraFieldMapping.MapType(issueType, null));

    [Theory]
    [InlineData("Lowest", "low")]
    [InlineData("Low", "low")]
    [InlineData("High", "high")]
    [InlineData("Mystery", "medium")]
    [InlineData(null, "medium")]
    public void MapsPriorities(string? priority, string expected) =>
        Assert.Equal(expected, JiraFieldMapping.MapPriority(priority));

    [Fact]
    public void CutsTitleTagsAndDropsLabelsPastTheCap()
    {
        var labels = Enumerable.Range(0, 25).Select(i => new string('x', 50) + i).ToList();
        var issue = Issue(new string('t', 400), "Task", "Medium", labels, null);

        var mapped = JiraFieldMapping.Map(issue, null, null);

        Assert.Equal(300, mapped.Title.Length);
        Assert.Equal(20, mapped.Tags.Count);
        Assert.All(mapped.Tags, tag => Assert.Equal(40, tag.Length));
        Assert.Null(mapped.Points);
        Assert.False(mapped.ClearPoints);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, null)]
    [InlineData(8, 8)]
    [InlineData(0, null)]
    public void SnapsStoryPointsOnlyWhenTheFieldIsConfigured(int raw, int? expected)
    {
        var issue = Issue("A", "Task", "Medium", [], raw);
        var points = JiraFieldMapping.MapPoints(issue, "customfield_10016", out var clear);
        Assert.True(clear);
        Assert.Equal(expected, points);
        Assert.Null(JiraFieldMapping.MapPoints(issue, null, out var untouched));
        Assert.False(untouched);
    }

    [Fact]
    public void MatchesALaneByNameAndParksAnUnknownStatus()
    {
        var lanes = new List<BoardColumnRecord>
        {
            Lane("backlog", "Backlog"),
            Lane("review", "Review"),
            Lane("overflow", "Jira")
        };

        var matched = JiraFieldMapping.MatchLane("review", "backlog", lanes, "overflow");
        Assert.Equal(JiraLaneMatch.Matched, matched.Match);
        Assert.Equal("review", matched.ColumnId);
        Assert.True(matched.Comment);

        var same = JiraFieldMapping.MatchLane("BACKLOG", "backlog", lanes, "overflow");
        Assert.Equal(JiraLaneMatch.AlreadyThere, same.Match);
        Assert.False(same.Comment);

        var parked = JiraFieldMapping.MatchLane("In QA", "backlog", lanes, "overflow");
        Assert.Equal(JiraLaneMatch.Overflow, parked.Match);
        Assert.Equal("overflow", parked.ColumnId);

        var ambiguous = JiraFieldMapping.MatchLane("Review", "backlog",
            [Lane("a", "Review"), Lane("b", "Review")], null);
        Assert.Equal(JiraLaneMatch.Unresolved, ambiguous.Match);
        Assert.Null(ambiguous.ColumnId);
    }

    private static JiraIssue Issue(string summary, string type, string? priority, IReadOnlyList<string> labels, double? points) =>
        new("10001", "PROJ-1", DateTime.UnixEpoch, summary, "body", "Backlog", type, priority, labels, "Ada Lovelace", points);

    private static BoardColumnRecord Lane(string id, string name) =>
        new(id, "project", name, 0, "#000", DateTime.UnixEpoch, DateTime.UnixEpoch, "board");
}
