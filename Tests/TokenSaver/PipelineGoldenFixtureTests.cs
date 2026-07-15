using System.Runtime.CompilerServices;
using TokenSaver.Minify;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Golden tests for the full two-stage pipeline <c>P = Condense ∘ Minify</c> under the Medium and
/// High tier presets, plus the cross-stage idempotency property the condenser's class doc argues
/// (<c>P(P(x)) == P(x)</c>) — asserted here over both stages' adversarial corpora and every golden
/// fixture, not just argued.
///
/// Fixtures live in <c>Fixtures\Pipeline\</c> (a subdirectory so the minifier corpus's
/// every-input-has-an-expected discovery invariant stays untouched) as
/// <c>&lt;name&gt;.input.txt</c> + <c>&lt;name&gt;.&lt;tier&gt;.expected.txt</c>. Expected files
/// were derived from the transform RULES (head/tail arithmetic, marker syntax), not by running the
/// condenser — they pin behavior. Same raw-byte discipline as the minifier fixtures: real control
/// bytes, LF framing, <c>-text</c> in .gitattributes, no newline translation anywhere.
/// </summary>
public class PipelineGoldenFixtureTests
{
    private static readonly string PipelineFixtureDir =
        Path.Combine(GetFixtureDir(), "Pipeline");

    private static string GetFixtureDir([CallerFilePath] string? callerPath = null)
        => Path.Combine(Path.GetDirectoryName(callerPath)!, "Fixtures");

    private static readonly (string Suffix, TokenSaverLevel Level)[] Tiers =
    [
        ("medium", TokenSaverLevel.Medium),
        ("high", TokenSaverLevel.High),
    ];

    private static string RunPipeline(string input, TokenSaverLevel level)
    {
        var (flags, condense, _) = TokenSaverPresets.For(level);
        return OutputCondenser.Condense(OutputMinifier.Minify(input, flags), condense);
    }

    public static TheoryData<string, string> TierCases()
    {
        const string suffix = ".input.txt";
        var data = new TheoryData<string, string>();
        foreach (var path in Directory.GetFiles(PipelineFixtureDir, "*" + suffix)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path)[..^suffix.Length];
            foreach (var (tier, _) in Tiers)
            {
                if (File.Exists(Path.Combine(PipelineFixtureDir, $"{name}.{tier}.expected.txt")))
                    data.Add(name, tier);
            }
        }
        return data;
    }

    [Fact]
    public void Corpus_IsPresent_AndEveryInputHasATierExpected()
    {
        Assert.True(Directory.Exists(PipelineFixtureDir), $"Missing fixture dir: {PipelineFixtureDir}");

        var inputs = Directory.GetFiles(PipelineFixtureDir, "*.input.txt");
        Assert.True(inputs.Length >= 2, $"Expected at least 2 pipeline fixtures, found {inputs.Length}");

        foreach (var input in inputs)
        {
            var name = Path.GetFileName(input)[..^".input.txt".Length];
            Assert.True(
                Tiers.Any(t => File.Exists(
                    Path.Combine(PipelineFixtureDir, $"{name}.{t.Suffix}.expected.txt"))),
                $"No tier expected file for {name}");
        }
    }

    [Theory]
    [MemberData(nameof(TierCases))]
    public void Fixture_CondensesToExpected(string name, string tier)
    {
        var input = File.ReadAllText(Path.Combine(PipelineFixtureDir, $"{name}.input.txt"));
        var expected = File.ReadAllText(Path.Combine(PipelineFixtureDir, $"{name}.{tier}.expected.txt"));
        var level = Tiers.Single(t => t.Suffix == tier).Level;

        Assert.Equal(expected, RunPipeline(input, level));
    }

    [Theory]
    [InlineData(TokenSaverLevel.Medium)]
    [InlineData(TokenSaverLevel.High)]
    public void Properties_PipelineIsIdempotentOverCorporaAndFixtures(TokenSaverLevel level)
    {
        var inputs = OutputMinifierTests.AdversarialInputs
            .Concat(OutputCondenserTests.AdversarialInputs())
            .Concat(Directory.GetFiles(GetFixtureDir(), "*.input.txt").Select(File.ReadAllText))
            .Concat(Directory.GetFiles(PipelineFixtureDir, "*.input.txt").Select(File.ReadAllText));

        foreach (var input in inputs)
        {
            var once = RunPipeline(input, level);

            Assert.True(once == RunPipeline(once, level),
                $"Pipeline not idempotent at {level} for input of length {input.Length}");
            Assert.True(once == RunPipeline(input, level),
                $"Pipeline not deterministic at {level} for input of length {input.Length}");
            Assert.True(once.Length <= input.Length,
                $"Pipeline grew output at {level}: {input.Length} → {once.Length}");
        }
    }
}
