using VibeRails.Services.AzureDevOps;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoBoardLinkTests
{
    [Fact]
    public void ABoardLink_GivesTheOrganizationProjectTeamAndLevel_DecodingSpacesAndDroppingTheViewQuery()
    {
        var link = AdoBoardLink.Parse(
            "https://dev.azure.com/contoso/Fabrikam%20Fiber/_boards/board/t/Fabrikam%20Fiber%20Team/Stories?System.AssignedTo=me#view");

        Assert.Equal("contoso", link.Organization);
        Assert.Equal("Fabrikam Fiber", link.Project);
        Assert.Equal("Fabrikam Fiber Team", link.Team);
        Assert.Equal("Stories", link.BacklogLevel);
        Assert.Null(link.QueryId);
        Assert.Equal("https://dev.azure.com/contoso/Fabrikam%20Fiber/_boards/board/t/Fabrikam%20Fiber%20Team/Stories", link.Link);
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Web/_backlogs/backlog/Web%20Team/Backlog%20items/", "Web Team", "Backlog items")]
    [InlineData("https://dev.azure.com/contoso/Web/_boards/board/t/Web Team/Epics/", "Web Team", "Epics")]
    [InlineData("https://DEV.AZURE.COM/contoso/Web/_BOARDS/Board/T/Core/Features", "Core", "Features")]
    public void BacklogAndBoardForms_GiveTheTeamBoard(string url, string team, string level)
    {
        var link = AdoBoardLink.Parse(url);
        Assert.Equal(team, link.Team);
        Assert.Equal(level, link.BacklogLevel);
        Assert.StartsWith("https://dev.azure.com/contoso/Web/_boards/board/t/", link.Link, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Web/_queries/query/0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0/")]
    [InlineData("https://dev.azure.com/contoso/Web/_queries/query-edit/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")]
    public void QueryLinks_GiveTheQueryIdInCanonicalForm(string url)
    {
        var link = AdoBoardLink.Parse(url);
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", link.QueryId);
        Assert.Null(link.Team);
        Assert.Null(link.BacklogLevel);
        Assert.Equal("https://dev.azure.com/contoso/Web/_queries/query/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0/", link.Link);
    }

    [Theory]
    [InlineData("http://dev.azure.com/contoso/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://contoso.visualstudio.com/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://dev.azure.com.evil.example/contoso/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://tfs.contoso.local/tfs/DefaultCollection/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://user:secret@dev.azure.com/contoso/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://dev.azure.com:8443/contoso/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://dev.azure.com/contoso")]
    [InlineData("https://dev.azure.com/contoso/Web")]
    [InlineData("https://dev.azure.com/contoso/Web/_workitems/edit/12")]
    [InlineData("https://dev.azure.com/contoso/Web/_boards/board/t/Core")]
    [InlineData("https://dev.azure.com/contoso/Web/_boards/board/t/Core/Stories/extra")]
    [InlineData("https://dev.azure.com/contoso/Web/_queries/query/not-a-guid/")]
    [InlineData("https://dev.azure.com/-contoso/Web/_boards/board/t/Core/Stories")]
    [InlineData("https://dev.azure.com/contoso/Web/_boards/board/t/Core%2F..%2F..%2F_apis/Stories")]
    [InlineData("https://dev.azure.com/contoso/Web/_boards/board/t/..%2E/Stories")]
    [InlineData("https://dev.azure.com/contoso/%2E%2E/_boards/board/t/Core/Stories")]
    [InlineData("https://dev.azure.com/contoso/Web/_boards/board/t/Core%3FapiKey%3D1/Stories")]
    [InlineData("not a link")]
    [InlineData("")]
    public void EveryOtherHostOrForm_IsRefusedWithAMessage(string url)
    {
        var error = Assert.Throws<AdoConfigException>(() => AdoBoardLink.Parse(url));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void AnotherHost_SaysOnlyDevAzureComIsSupported()
    {
        var error = Assert.Throws<AdoConfigException>(() => AdoBoardLink.Parse("https://contoso.visualstudio.com/Web/_boards/board/t/Core/Stories"));
        Assert.Contains("dev.azure.com", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkItemUrls_EscapeTheProject()
    {
        Assert.Equal("https://dev.azure.com/contoso/Fabrikam%20Fiber/_workitems/edit/42",
            AdoBoardLink.WorkItemUrl("contoso", "Fabrikam Fiber", 42));
        Assert.Equal("#42", AdoFieldMapping.Key(42));
    }
}
