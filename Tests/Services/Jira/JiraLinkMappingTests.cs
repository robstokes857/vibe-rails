using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraLinkMappingTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("lane:col_1;points:", null, null)]
    [InlineData("lane:overflow;points:customfield_10016", "customfield_10016", null)]
    [InlineData("points:customfield_10016", "customfield_10016", null)]
    [InlineData("status:To Do", null, "To Do")]
    [InlineData("points:customfield_10020;status:In Review; really", "customfield_10020", "In Review; really")]
    public void ParseReadsCurrentAndPreUpgradeMappings(string? mapping, string? points, string? status)
    {
        Assert.Equal((points, status), JiraLinkMapping.Parse(mapping));
    }

    [Fact]
    public void SerializeRoundTrips()
    {
        Assert.Equal("status:Done", JiraLinkMapping.Serialize(null, "Done"));
        Assert.Equal("points:customfield_10016;status:Done", JiraLinkMapping.Serialize("customfield_10016", "Done"));
        Assert.Equal(("customfield_10016", "Done"), JiraLinkMapping.Parse(JiraLinkMapping.Serialize("customfield_10016", "Done")));
    }
}
