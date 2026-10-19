using VibeRails.Services.AzureDevOps;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoWiqlTests
{
    private static readonly AdoTeamFieldValues Team = new("System.AreaPath",
    [
        new AdoTeamFieldValue("Web\\Core", false),
        new AdoTeamFieldValue("Web\\Ops", false),
        new AdoTeamFieldValue("Web\\Tom's Team", true)
    ]);

    [Fact]
    public void ABoardQuery_FiltersTypesTeamAndRemoved_DoublingEveryQuote_NewestFirst()
    {
        var wiql = AdoWiql.BoardQuery(["User Story", "O'Brien's Bug"], null, Team, [], skipOldDone: false, narrowing: null);

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
            + " AND [System.WorkItemType] IN ('User Story', 'O''Brien''s Bug')"
            + " AND ([System.AreaPath] IN ('Web\\Core', 'Web\\Ops') OR [System.AreaPath] UNDER 'Web\\Tom''s Team')"
            + " AND [System.State] <> 'Removed'"
            + " ORDER BY [System.ChangedDate] DESC",
            wiql);
    }

    [Fact]
    public void SkipOldDone_KeepsDoneWorkItemsOnlyWhileRecentlyChanged_AndNarrowingIsWrapped()
    {
        var wiql = AdoWiql.BoardQuery(["User Story", "Bug"], null, Team,
            [("User Story", "Closed"), ("Bug", "Closed")], skipOldDone: true, narrowing: "[System.Tags] CONTAINS 'ui'");

        Assert.Contains(
            " AND ((([System.WorkItemType] <> 'User Story' OR [System.State] <> 'Closed')"
            + " AND ([System.WorkItemType] <> 'Bug' OR [System.State] <> 'Closed'))"
            + " OR [System.ChangedDate] >= @today - 30)",
            wiql, StringComparison.Ordinal);
        Assert.EndsWith(" AND ([System.Tags] CONTAINS 'ui') ORDER BY [System.ChangedDate] DESC", wiql, StringComparison.Ordinal);

        // Off, or with no done column to say what is done, nothing is left out.
        Assert.DoesNotContain("@today", AdoWiql.BoardQuery(["Bug"], null, Team, [("Bug", "Closed")], false, null), StringComparison.Ordinal);
        Assert.DoesNotContain("@today", AdoWiql.BoardQuery(["Bug"], null, Team, [], true, null), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheBoardsTypes_TheBacklogCategoryStandsIn_AndAnExactTeamFieldIsOneClause()
    {
        var single = new AdoTeamFieldValues("Custom.Squad", [new AdoTeamFieldValue("Blue", true)]);
        var wiql = AdoWiql.BoardQuery([], "Microsoft.RequirementCategory", single, [], true, null);

        Assert.Contains("[System.WorkItemType] IN GROUP 'Microsoft.RequirementCategory'", wiql, StringComparison.Ordinal);
        // includeChildren only means anything for an area path.
        Assert.Contains(" AND [Custom.Squad] IN ('Blue') AND ", wiql, StringComparison.Ordinal);
        Assert.Equal("Microsoft.EpicCategory", AdoPullService.TypeCategory("Epics"));
        Assert.Equal("Microsoft.FeatureCategory", AdoPullService.TypeCategory(" features "));
        Assert.Equal("Microsoft.RequirementCategory", AdoPullService.TypeCategory("Backlog items"));
    }

    [Fact]
    public void ATeamFieldThatIsNotAFieldName_OrNoTeamValues_IsRefused()
    {
        Assert.Throws<AdoConfigException>(() => AdoWiql.BoardQuery(["Bug"], null,
            new AdoTeamFieldValues("System.AreaPath] = 'x' OR [System.Id", [new AdoTeamFieldValue("Web", false)]), [], false, null));
        Assert.Throws<AdoConfigException>(() => AdoWiql.BoardQuery(["Bug"], null, new AdoTeamFieldValues("System.AreaPath", []), [], false, null));
        Assert.Throws<AdoConfigException>(() => AdoWiql.BoardQuery([], null, Team, [], false, null));
    }

    [Theory]
    [InlineData("  [System.Tags] CONTAINS 'ui'  ", "[System.Tags] CONTAINS 'ui'")]
    [InlineData("[System.Title] CONTAINS 'order by'", "[System.Title] CONTAINS 'order by'")]
    [InlineData("[System.Title] CONTAINS 'it''s (open'", "[System.Title] CONTAINS 'it''s (open'")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Narrowing_IsTrimmed_AndQuotedTextIsNotParsed(string? input, string? expected) =>
        Assert.Equal(expected, AdoWiql.ValidateNarrowing(input));

    [Theory]
    [InlineData("[System.State] = 'Active' ORDER BY [System.Id]")]
    [InlineData("[System.State] = 'Active') OR ([System.Id] > 0")]
    [InlineData("([System.State] = 'Active'")]
    [InlineData("[System.Title] = 'unclosed")]
    public void Narrowing_ThatCouldWidenOrReorderTheQuery_IsRefused(string input) =>
        Assert.Throws<AdoConfigException>(() => AdoWiql.ValidateNarrowing(input));

    [Fact]
    public void OversizedNarrowing_IsRefused() =>
        Assert.Throws<AdoConfigException>(() => AdoWiql.ValidateNarrowing(new string('x', AdoWiql.MaxNarrowingLength + 1)));

    [Theory]
    [InlineData("System.AreaPath", true)]
    [InlineData("Microsoft.VSTS.Scheduling.StoryPoints", true)]
    [InlineData("WEF_6CB513B6E70E43499D9FC94E5BBFB784_Kanban.Column", true)]
    [InlineData("Custom.Points", true)]
    [InlineData("StoryPoints", false)]
    [InlineData("System.Area Path", false)]
    [InlineData("System.AreaPath]", false)]
    [InlineData("", false)]
    public void FieldReferences(string value, bool expected) => Assert.Equal(expected, AdoWiql.IsFieldReference(value));
}
