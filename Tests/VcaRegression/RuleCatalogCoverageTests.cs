using System.Text;
using VibeRails.Services;
using VibeRails.Services.Git;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// Guards the fixture directory itself: every rule in the catalog has at least one scenario that
/// declares it, and the byte-exact scenarios are still byte-exact in this checkout.
/// </summary>
public sealed class RuleCatalogCoverageTests
{
    [Fact]
    public void EveryCatalogRule_IsDeclaredBySomeFixture()
    {
        var covered = new HashSet<Rule>();
        var rulesFiles = Directory
            .EnumerateFiles(VcaRegressionRepository.FixturesRoot, VcaRegressionRepository.FixtureRulesFileName, SearchOption.AllDirectories)
            .ToList();
        Assert.NotEmpty(rulesFiles);

        foreach (var file in rulesFiles)
        {
            var text = Encoding.UTF8
                .GetString(File.ReadAllBytes(file))
                .TrimStart('\uFEFF')
                .Replace(VcaRegressionRepository.LevelToken, "STOP", StringComparison.Ordinal);
            foreach (var line in AgentRuleSectionReader.Read(text))
            {
                if (RuleParser.TryParse(line.RuleText, out var rule))
                {
                    covered.Add(rule);
                }
            }
        }

        var missing = Enum.GetValues<Rule>().Where(rule => !covered.Contains(rule)).ToList();
        Assert.True(
            missing.Count == 0,
            "Catalog rules with no regression fixture: " + string.Join(", ", missing));
    }

    [Fact]
    public void LevelToken_OnlyAppearsInRulesFiles()
    {
        var leaks = Directory
            .EnumerateFiles(VcaRegressionRepository.FixturesRoot, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).Equals(VcaRegressionRepository.FixtureRulesFileName, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(VcaRegressionRepository.LevelToken, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(leaks);
    }

    [Fact]
    public void NoFixtureIsNamedLikeALiveRulesFile()
    {
        // A vc.rules.md anywhere under Fixtures/ is live policy for THIS repository: Git Guard
        // reads every one in the index, scoped to its directory, and the STOP fixtures would
        // block the commit that adds them. Fixtures are stored as vc.rules.fixture.md and the
        // harness renames them on the way into the temp repository.
        var live = Directory
            .EnumerateFiles(VcaRegressionRepository.FixturesRoot, "vc.rules.md", SearchOption.AllDirectories)
            .ToList();

        Assert.Empty(live);
    }

    [Fact]
    public void ByteExactFixtures_AreStillByteExact()
    {
        // text=auto once stripped every CR from committed fixtures and broke golden tests only on
        // a fresh clone. These three scenarios are the canaries for the -text attribute.
        var crlf = File.ReadAllBytes(Fixture("rule-file-parsing/crlf-line-endings/staged/vc.rules.fixture.md"));
        Assert.Contains((byte)'\r', crlf);
        Assert.DoesNotContain("\n", Encoding.ASCII.GetString(crlf).Replace("\r\n", string.Empty), StringComparison.Ordinal);

        var bom = File.ReadAllBytes(Fixture("rule-file-parsing/utf8-bom/staged/vc.rules.fixture.md"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bom.Take(3).ToArray());

        var binary = File.ReadAllBytes(Fixture("log-file-changes-over-5/binary-file/staged/assets/blob.bin"));
        Assert.Contains((byte)0, binary);
    }

    [Fact]
    public async Task FixtureDirectory_IsMarkedNoTextInGitAttributes()
    {
        var root = GitCli.FindRoot(VcaRegressionRepository.FixturesRoot);
        if (root is null)
        {
            return; // Not a git checkout (source archive); nothing to verify.
        }

        var relative = Path
            .GetRelativePath(root, Fixture("rule-file-parsing/crlf-line-endings/staged/vc.rules.fixture.md"))
            .Replace('\\', '/');
        var result = await GitCli.RunAsync(
            root,
            ["check-attr", "text", "--", relative],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith(": text: unset", result.StdOut.Trim());
    }

    private static string Fixture(string relativePath) =>
        Path.Combine(VcaRegressionRepository.FixturesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
