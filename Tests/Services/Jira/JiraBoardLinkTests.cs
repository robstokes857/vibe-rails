using VibeRails.Services.Board;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraBoardLinkTests
{
    [Fact]
    public void RobsTeamManagedBoardLink_GivesTheSiteProjectAndBoard_AndDropsTheBoardViewQuery()
    {
        var link = JiraBoardLink.Parse(
            "https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1?filter=&groupBy=none&atlOrigin=eyJpIjoiOWI3NmJkMjE1Yjc1NDVhZjhlNDk5NDhkMmYxZjcyNDkiLCJwIjoiaiJ9");

        Assert.Equal("https://robstokes857.atlassian.net", link.Origin);
        Assert.Equal("1", link.BoardId);
        Assert.Equal("SCRUM", link.ProjectKey);
        Assert.Null(link.Jql);
        Assert.Null(link.FilterId);
        Assert.Equal("https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1", link.Link);
        Assert.Equal("project = \"SCRUM\"", link.SourceJql);
    }

    [Theory]
    [InlineData("https://acme.atlassian.net/jira/software/c/projects/OPS/boards/12", "12", "OPS", "https://acme.atlassian.net/jira/software/c/projects/OPS/boards/12")]
    [InlineData("https://acme.atlassian.net/jira/software/c/projects/OPS/boards/12/backlog?epics=visible", "12", "OPS", "https://acme.atlassian.net/jira/software/c/projects/OPS/boards/12")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/ops/boards/3/timeline", "3", "OPS", "https://acme.atlassian.net/jira/software/projects/ops/boards/3")]
    [InlineData("https://acme.atlassian.net/secure/RapidBoard.jspa?rapidView=7&projectKey=WEB&view=planning", "7", "WEB", "https://acme.atlassian.net/secure/RapidBoard.jspa?rapidView=7")]
    [InlineData("https://acme.atlassian.net/secure/RapidBoard.jspa?rapidView=7", "7", null, "https://acme.atlassian.net/secure/RapidBoard.jspa?rapidView=7")]
    public void BoardLinks_GiveTheBoardId(string url, string boardId, string? projectKey, string saved)
    {
        var link = JiraBoardLink.Parse(url);
        Assert.Equal(boardId, link.BoardId);
        Assert.Equal(projectKey, link.ProjectKey);
        Assert.Equal(saved, link.Link);
    }

    [Theory]
    [InlineData("https://acme.atlassian.net/jira/core/projects/OPS/board", "OPS")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/OPS/list", "OPS")]
    [InlineData("https://acme.atlassian.net/jira/software/c/projects/OPS/issues", "OPS")]
    [InlineData("https://acme.atlassian.net/browse/OPS-123", "OPS")]
    [InlineData("https://acme.atlassian.net/browse/ops", "OPS")]
    public void ProjectAndIssueLinks_PullTheProject(string url, string projectKey)
    {
        var link = JiraBoardLink.Parse(url);
        Assert.Null(link.BoardId);
        Assert.Equal(projectKey, link.ProjectKey);
        Assert.Equal($"project = \"{projectKey}\"", link.SourceJql);
    }

    [Fact]
    public void IssueSearchLinks_KeepTheirJqlOrFilter()
    {
        var jql = JiraBoardLink.Parse("https://acme.atlassian.net/issues/?jql=project%20%3D%20OPS%20ORDER%20BY%20created%20DESC");
        Assert.Equal("project = OPS ORDER BY created DESC", jql.Jql);
        Assert.Equal(jql.Jql, jql.SourceJql);
        Assert.Equal("https://acme.atlassian.net/issues?jql=project%20%3D%20OPS%20ORDER%20BY%20created%20DESC", jql.Link);

        var filter = JiraBoardLink.Parse("https://acme.atlassian.net/issues/?filter=10001");
        Assert.Equal("10001", filter.FilterId);
        Assert.Equal("filter = 10001", filter.SourceJql);

        // A system filter (negative id) is not a saved filter id; the link names nothing else.
        Assert.Throws<JiraConfigException>(() => JiraBoardLink.Parse("https://acme.atlassian.net/issues/?filter=-4"));
    }

    [Theory]
    [InlineData("", "https link")]
    [InlineData("acme.atlassian.net/jira/software/projects/OPS/boards/1", "https link")]
    [InlineData("http://acme.atlassian.net/jira/software/projects/OPS/boards/1", "https link")]
    [InlineData("https://user:token@acme.atlassian.net/jira/software/projects/OPS/boards/1", "https link")]
    [InlineData("https://acme.atlassian.net", "not just the site")]
    [InlineData("https://acme.atlassian.net/", "not just the site")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/OPS/boards/abc", "board number")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/OPS/boards/1%2F..%2Fadmin", "board number")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/OPS/boards", "board number")]
    [InlineData("https://acme.atlassian.net/secure/RapidBoard.jspa?rapidView=1%20OR%201", "board number")]
    [InlineData("https://acme.atlassian.net/jira/your-work", "isn't a Jira board")]
    [InlineData("https://acme.atlassian.net/browse/1-2", "isn't a Jira board")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/O\"P/list", "isn't a Jira board")]
    public void Rejects_WithAReadableReason(string url, string reason)
    {
        var error = Assert.Throws<JiraConfigException>(() => JiraBoardLink.Parse(url));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectKeyThatIsNotAKeyIsDropped_ButTheBoardStays()
    {
        var link = JiraBoardLink.Parse("https://acme.atlassian.net/jira/software/projects/A%22B/boards/4");
        Assert.Equal("4", link.BoardId);
        Assert.Null(link.ProjectKey);
        Assert.Null(link.SourceJql);
    }
}

public sealed class JiraJqlTests
{
    [Fact]
    public void AndParenthesisesEachClause_AndSkipsBlanks()
    {
        Assert.Null(JiraJql.And(null, " ", ""));
        Assert.Equal("a = 1", JiraJql.And(null, " a = 1 "));
        Assert.Equal("(a = 1 OR b = 2) AND (c = 3)", JiraJql.And("a = 1 OR b = 2", null, "c = 3"));
    }

    [Fact]
    public void NarrowKeepsTheQuerysOrderByAtTheEnd()
    {
        Assert.Equal("project = OPS", JiraJql.Narrow("project = OPS", null));
        Assert.Equal("(project = OPS) AND (assignee = me) ORDER BY Rank ASC",
            JiraJql.Narrow("project = OPS ORDER BY Rank ASC", "assignee = me"));
        Assert.Equal("assignee = me order by created", JiraJql.Narrow("order by created", "assignee = me"));
        // "order by" inside a quoted value is text, not a clause.
        Assert.Equal("(summary ~ \"order by\") AND (x = 1)", JiraJql.Narrow("summary ~ \"order by\"", "x = 1"));
    }

    [Fact]
    public void NarrowingRefusesOrderBy_AndBlankMeansNone()
    {
        Assert.Null(JiraJql.ValidateNarrowing("   "));
        Assert.Equal("labels = ui", JiraJql.ValidateNarrowing(" labels = ui "));
        Assert.Equal("summary ~ 'Order By form'", JiraJql.ValidateNarrowing("summary ~ 'Order By form'"));
        Assert.Throws<JiraConfigException>(() => JiraJql.ValidateNarrowing("labels = ui ORDER BY rank"));
        Assert.Throws<JiraConfigException>(() => JiraJql.ValidateNarrowing(new string('x', JiraJql.MaxNarrowingLength + 1)));
    }
}

public sealed class JiraColumnMapTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static BoardColumnRecord Lane(string id, string name) => new(id, "p", name, 0, "#000", Now, Now, "brd");

    private static readonly IReadOnlyList<BoardColumnRecord> RobsLanes =
        [Lane("col_backlog", "Backlog"), Lane("col_progress", "In Progress"), Lane("col_review", "Review"), Lane("col_done", "Done")];

    [Fact]
    public void ScrumColumns_MatchByName_FirstToFirstLane_LastToDone()
    {
        var resolved = JiraColumnMap.Resolve(["To Do", "In Progress", "Done"], [], RobsLanes, null);
        Assert.Equal(["col_backlog", "col_progress", "col_done"], resolved.Select(column => column.LaneId));
        Assert.All(resolved, column => Assert.True(column.Automatic));
    }

    [Fact]
    public void KanbanColumns_MatchWholeWords_AndAnUnmatchedMiddleColumnGoesToOverflow()
    {
        var lanes = RobsLanes.Append(Lane("col_jira", "Jira")).ToList();
        var resolved = JiraColumnMap.Resolve(
            ["Backlog", "Selected for Development", "In Progress", "In Review", "Closed"], [], lanes, "col_jira");
        Assert.Equal(["col_backlog", null, "col_progress", "col_review", "col_done"], resolved.Select(column => column.LaneId));
    }

    [Fact]
    public void WithoutADoneLane_TheLastColumnGoesToTheLastLane_AndTheOverflowLaneIsNeverAutomatic()
    {
        var lanes = new List<BoardColumnRecord> { Lane("a", "Todo"), Lane("b", "Doing"), Lane("jira", "Jira") };
        var resolved = JiraColumnMap.Resolve(["To Do", "Finished"], [], lanes, null);
        Assert.Equal(["a", "b"], resolved.Select(column => column.LaneId));
    }

    [Fact]
    public void APickWinsWhileItsLaneExists_ThenFallsBackToAutomatic()
    {
        var picks = JiraColumnMap.WithChoices(
            JiraColumnMap.WithColumns([], ["To Do", "In Progress", "Done"]),
            new Dictionary<string, string?> { ["in progress"] = "col_review", ["Done"] = "", ["Later"] = "col_gone" });
        Assert.Equal(["To Do", "In Progress", "Done", "Later"], picks.Select(column => column.Name));

        var resolved = JiraColumnMap.Resolve(["To Do", "In Progress", "Done", "Later"], picks, RobsLanes, null);
        Assert.Equal("col_review", resolved[1].LaneId);
        Assert.False(resolved[1].Automatic);
        Assert.Equal("col_done", resolved[2].LaneId);
        Assert.True(resolved[3].Automatic);
    }

    [Fact]
    public void TheSavedMapRoundTrips_AndKeepsPicksWhenTheBoardsColumnsAreReadAgain()
    {
        var saved = JiraColumnMap.Serialize([new JiraColumnChoice("To Do", null), new JiraColumnChoice("In \"QA\"", "col_review")]);
        var parsed = JiraColumnMap.Parse(saved);
        Assert.Equal([new JiraColumnChoice("To Do", null), new JiraColumnChoice("In \"QA\"", "col_review")], parsed);

        var refreshed = JiraColumnMap.WithColumns(parsed, ["Backlog", "in \"qa\"", "Backlog"]);
        Assert.Equal([new JiraColumnChoice("Backlog", null), new JiraColumnChoice("in \"qa\"", "col_review")], refreshed);

        Assert.Empty(JiraColumnMap.Parse("{not json"));
        Assert.Null(JiraColumnMap.Serialize([]));
    }
}
