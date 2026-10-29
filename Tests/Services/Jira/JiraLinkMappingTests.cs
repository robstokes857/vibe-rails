using VibeRails.Services.Board.Tracker;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraLinkMappingTests
{
    // The format an older VibeRails reads to decide an unchanged item is already applied (VIBE-236).
    [Fact]
    public void SerializeWritesTheFormatOlderVersionsRead()
    {
        Assert.Equal("status:Done", TrackerLinkMapping.Serialize(null, "Done"));
        Assert.Equal("points:customfield_10016;status:Done", TrackerLinkMapping.Serialize("customfield_10016", "Done"));
        Assert.Equal("points:customfield_10020;status:In Review; really", TrackerLinkMapping.Serialize("customfield_10020", "In Review; really"));
    }
}
