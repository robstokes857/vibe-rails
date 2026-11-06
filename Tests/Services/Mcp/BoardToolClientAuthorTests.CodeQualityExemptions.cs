using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolClientAuthorTests
{
    [Theory]
    [InlineData("codex-mcp-client", true)]
    [InlineData("Agent", false)]
    public async Task QualityExemptionWireToolsRequireAndKeepClientAttribution(string clientName, bool allowed)
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Fixture"), Ct);
        var client = await ConnectAsync(clientName);
        var result = await CallAsync(client, "exempt_code_quality_file", new()
        {
            ["path"] = "generated.cs", ["reason"] = "Generated source", ["scope"] = "repository", ["card"] = card.Key
        });
        if (!allowed)
        {
            Assert.StartsWith("FAIL: this MCP client did not say which agent", result);
            Assert.Empty(await _store.GetCodeQualityExemptionsAsync(_project, card.Id, Ct));
            return;
        }
        Assert.StartsWith("Exemption ", result);
        var entry = Assert.Single(await _store.GetCodeQualityExemptionsAsync(_project, card.Id, Ct));
        Assert.Equal("Codex", entry.Author.Label);
        Assert.Null(entry.CardId);
        Assert.Contains(entry.Id, await CallAsync(client, "list_code_quality_exemptions", new() { ["card"] = card.Key }));
        Assert.StartsWith("Revoked ", await CallAsync(client, "revoke_code_quality_exemption", new() { ["exemptionId"] = entry.Id, ["card"] = card.Key }));
    }
}
