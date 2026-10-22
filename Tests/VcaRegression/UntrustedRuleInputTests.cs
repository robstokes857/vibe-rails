using System.Diagnostics;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests.VcaRegression;

public sealed class UntrustedRuleInputTests
{
    [Fact]
    public void OversizedLineFailsWithoutAllocatingOrMatchingIt()
    {
        var content = "## Vibe Rails Rules\n-" + new string(' ', 8 * 1024 * 1024);
        var sw = Stopwatch.StartNew();
        Assert.Throws<InvalidDataException>(() => AgentRuleSectionReader.Read(content));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Rule rejection took {sw.Elapsed}");
    }

    [Fact]
    public void SpaceOnlyAndNearMatchingSuffixesStayFastAtTheLimit()
    {
        var hostile = "-" + new string(' ', AgentRuleSectionReader.MaxLineLength - 10) + "(STOPx)";
        var content = "## Vibe Rails Rules\n" + string.Concat(Enumerable.Repeat(hostile + "\n", 200)) + "- Keep this (STOP)";
        var sw = Stopwatch.StartNew();
        var rules = AgentRuleSectionReader.Read(content);
        Assert.Equal(new AgentRuleLine("Keep this", "STOP"), rules[^1]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Bounded rule matching took {sw.Elapsed}");
    }

    [Fact]
    public async Task LineCountingDoesNotMaterializeAnUnboundedLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vca-large-line-{Guid.NewGuid():N}");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(path, new string(' ', 8 * 1024 * 1024) + "\r\nsecond\rthird\n", ct);
            var sw = Stopwatch.StartNew();
            Assert.Equal(3, await new FileReader().GetLineCountAsync(path, ct));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Line counting took {sw.Elapsed}");
        }
        finally { File.Delete(path); }
    }
}
