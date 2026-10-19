using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoFieldMappingTests
{
    [Fact]
    public void MapsTheDocumentedFields_AndLeavesAChoreAlone()
    {
        var item = Item("Bug", "<div>It <b>breaks</b></div>", priority: 1, tags: "ui; ;  login ", points: 5);

        var mapped = AdoFieldMapping.Map(item, AdoFields.StoryPoints, BoardCardTypes.Task);
        Assert.Equal("Fix sign-in", mapped.Title);
        Assert.Equal("It breaks", mapped.Description);
        Assert.Equal(BoardCardTypes.Bug, mapped.Type);
        Assert.Equal("critical", mapped.Priority);
        Assert.Equal(5, mapped.Points);
        Assert.False(mapped.ClearPoints);
        Assert.Equal(["ui", "login"], mapped.Tags);
        Assert.Equal("Active", mapped.State);
        Assert.Equal("Ada Lovelace", mapped.AssigneeDisplay);

        Assert.Equal(BoardCardTypes.Chore, AdoFieldMapping.Map(item, null, BoardCardTypes.Chore).Type);
    }

    [Fact]
    public void TitlesStopAt300Characters()
    {
        var mapped = AdoFieldMapping.Map(Item("Task", null) with { Title = "  " + new string('t', 400) }, null, null);
        Assert.Equal(300, mapped.Title.Length);
    }

    [Fact]
    public void ABugWithNoDescription_UsesItsReproSteps_AndAcceptanceCriteriaFollowUnderAHeading()
    {
        var bug = Item("Bug", "<div><br></div>") with
        {
            ReproStepsHtml = "<ol><li>Open it</li></ol>",
            AcceptanceCriteriaHtml = "<p>It opens</p>"
        };
        Assert.Equal("- Open it\n\n## Acceptance criteria\n\nIt opens", AdoFieldMapping.MapDescription(bug));

        // Only a bug falls back to repro steps.
        var story = bug with { WorkItemType = "User Story" };
        Assert.Equal("## Acceptance criteria\n\nIt opens", AdoFieldMapping.MapDescription(story));

        var described = bug with { DescriptionHtml = "<p>Written up</p>", AcceptanceCriteriaHtml = null };
        Assert.Equal("Written up", AdoFieldMapping.MapDescription(described));
    }

    [Theory]
    [InlineData("User Story", "feature")]
    [InlineData("Product Backlog Item", "feature")]
    [InlineData("Requirement", "feature")]
    [InlineData("Feature", "feature")]
    [InlineData("epic", "feature")]
    [InlineData("Bug", "bug")]
    [InlineData("Spike", "research-spike")]
    [InlineData("Task", "task")]
    [InlineData("Issue", "task")]
    [InlineData("Test Case", "task")]
    [InlineData(null, "task")]
    public void MapsWorkItemTypes(string? type, string expected) =>
        Assert.Equal(expected, AdoFieldMapping.MapType(type, null));

    [Theory]
    [InlineData(1, "critical")]
    [InlineData(2, "high")]
    [InlineData(3, "medium")]
    [InlineData(4, "low")]
    [InlineData(0, "medium")]
    [InlineData(null, "medium")]
    public void MapsPriorities(int? priority, string expected) =>
        Assert.Equal(expected, AdoFieldMapping.MapPriority(priority));

    [Fact]
    public void Points_SnapToFibonacci_AndAFieldWithNoValueClearsThem_WhileNoFieldLeavesThemAlone()
    {
        Assert.Equal(8, AdoFieldMapping.MapPoints(Item("User Story", null, points: 7.6), AdoFields.StoryPoints, out var clear));
        Assert.False(clear);
        Assert.Null(AdoFieldMapping.MapPoints(Item("User Story", null, points: 4), AdoFields.StoryPoints, out clear));
        Assert.True(clear);
        Assert.Null(AdoFieldMapping.MapPoints(Item("User Story", null), AdoFields.StoryPoints, out clear));
        Assert.True(clear);
        Assert.Null(AdoFieldMapping.MapPoints(Item("User Story", null, points: 5), null, out clear));
        Assert.False(clear);
    }

    [Fact]
    public void ThePointsFieldIsDetectedFromTheProcessTypes_WhenTheOrganizationHasIt()
    {
        var all = new AdoFieldSet(new HashSet<string>([AdoFields.StoryPoints, AdoFields.Effort, AdoFields.Size], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(AdoFields.StoryPoints, AdoFieldMapping.DetectPointsField(["Bug", "User Story"], all));
        Assert.Equal(AdoFields.Effort, AdoFieldMapping.DetectPointsField(["Product Backlog Item", "Bug"], all));
        Assert.Equal(AdoFields.Effort, AdoFieldMapping.DetectPointsField(["Issue"], all));
        Assert.Equal(AdoFields.Size, AdoFieldMapping.DetectPointsField(["Requirement"], all));
        Assert.Null(AdoFieldMapping.DetectPointsField(["Epic"], all));
        Assert.Null(AdoFieldMapping.DetectPointsField(["User Story"], new AdoFieldSet(new HashSet<string>())));
        Assert.Null(AdoFieldMapping.DetectPointsField(["User Story"], null));
    }

    [Fact]
    public void SystemBoardColumn_PlacesACardOnlyWhenTheLinkedBoardAgrees()
    {
        IReadOnlyList<AdoBoardColumn> columns =
        [
            Column("New", "incoming", ("User Story", "New")),
            Column("Active", "inProgress", ("User Story", "Active"), ("Bug", "Active")),
            Column("Code Review", "inProgress", ("User Story", "Active"), ("Bug", "Active")),
            Column("Closed", "outgoing", ("User Story", "Closed"), ("Bug", "Closed"))
        ];

        // Two columns share Active: the board column tells them apart.
        Assert.Equal(2, AdoPullService.ColumnIndex(columns, "code review", "User Story", "Active"));
        Assert.Equal(1, AdoPullService.ColumnIndex(columns, null, "User Story", "Active"));
        // Another team's column name, or one that doesn't map this state, falls back to the state mapping.
        Assert.Equal(1, AdoPullService.ColumnIndex(columns, "Doing", "Bug", "Active"));
        Assert.Equal(3, AdoPullService.ColumnIndex(columns, "New", "User Story", "Closed"));
        // No column maps it: the card is placed by state name instead.
        Assert.Equal(-1, AdoPullService.ColumnIndex(columns, "New", "Task", "New"));
    }

    private static AdoBoardColumn Column(string name, string type, params (string Type, string State)[] mappings) =>
        new(name, type, mappings.ToDictionary(pair => pair.Type, pair => pair.State, StringComparer.OrdinalIgnoreCase), false);

    private static AdoWorkItem Item(string type, string? descriptionHtml, int? priority = null, string? tags = null, double? points = null) =>
        new(7, 3, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "  Fix sign-in  ", type, " Active ", descriptionHtml, null, null,
            priority, tags, " Ada Lovelace ", null,
            points is double value ? new Dictionary<string, double> { [AdoFields.StoryPoints] = value } : new Dictionary<string, double>());
}
