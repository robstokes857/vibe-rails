using System.Runtime.CompilerServices;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Golden tests for the full two-stage pipeline <c>P = Condense ∘ Minify</c> under two stage
/// selections — every lossless stage plus dedupe, and the same plus truncation — chosen because
/// they are the two that make the condenser do something. Plus the cross-stage idempotency property
/// the condenser's class doc argues (<c>P(P(x)) == P(x)</c>), asserted here over both stages'
/// adversarial corpora and every golden fixture, not just argued.
///
/// Fixtures live in <c>Fixtures\Pipeline\</c> (a subdirectory so the minifier corpus's
/// every-input-has-an-expected discovery invariant stays untouched) as
/// <c>&lt;name&gt;.input.txt</c> + <c>&lt;name&gt;.&lt;variant&gt;.expected.txt</c>. Expected files
/// were derived from the transform RULES (head/tail arithmetic, marker syntax), not by running the
/// condenser — they pin behavior. Same raw-byte discipline as the minifier fixtures: real control
/// bytes, LF framing, <c>-text</c> in .gitattributes, no newline translation anywhere.
///
/// The <c>medium</c>/<c>high</c> variant names are the fixture FILENAME suffixes and are a wire
/// format on disk: renaming one orphans its expected file. They are historical (these were the old
/// Medium/High tier presets); the id sets below are what actually defines each variant now.
/// </summary>
public class PipelineGoldenFixtureTests
{
    private static readonly string PipelineFixtureDir =
        Path.Combine(GetFixtureDir(), "Pipeline");

    private static string GetFixtureDir([CallerFilePath] string? callerPath = null)
        => Path.Combine(Path.GetDirectoryName(callerPath)!, "Fixtures");

    // Every lossless stage + dedupe. No shape ids: shape filters key off a command the pipeline
    // never sees here, and enabling one would change these goldens.
    internal static readonly string[] MediumIds =
    [
        CompressionCatalog.CrCollapse, CompressionCatalog.AnsiStrip,
        CompressionCatalog.TrailingWhitespace, CompressionCatalog.BlankEdges,
        CompressionCatalog.BlankRuns, CompressionCatalog.DedupeLines,
    ];

    /// <summary>Medium plus the one lossy stage it withholds.</summary>
    internal static readonly string[] HighIds = [.. MediumIds, CompressionCatalog.TruncateLong];

    private static readonly (string Suffix, string[] Ids)[] Variants =
    [
        ("medium", MediumIds),
        ("high", HighIds),
    ];

    private static string RunPipeline(string input, string[] ids)
    {
        var plan = CompressionCatalog.Resolve(ids);
        return OutputCondenser.Condense(OutputMinifier.Minify(input, plan.Flags), plan.Condense);
    }

    public static TheoryData<string, string> VariantCases()
    {
        const string suffix = ".input.txt";
        var data = new TheoryData<string, string>();
        foreach (var path in Directory.GetFiles(PipelineFixtureDir, "*" + suffix)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path)[..^suffix.Length];
            foreach (var (variant, _) in Variants)
            {
                if (File.Exists(Path.Combine(PipelineFixtureDir, $"{name}.{variant}.expected.txt")))
                    data.Add(name, variant);
            }
        }
        return data;
    }

    [Fact]
    public void Corpus_IsPresent_AndEveryInputHasAVariantExpected()
    {
        Assert.True(Directory.Exists(PipelineFixtureDir), $"Missing fixture dir: {PipelineFixtureDir}");

        var inputs = Directory.GetFiles(PipelineFixtureDir, "*.input.txt");
        Assert.True(inputs.Length >= 2, $"Expected at least 2 pipeline fixtures, found {inputs.Length}");

        foreach (var input in inputs)
        {
            var name = Path.GetFileName(input)[..^".input.txt".Length];
            Assert.True(
                Variants.Any(v => File.Exists(
                    Path.Combine(PipelineFixtureDir, $"{name}.{v.Suffix}.expected.txt"))),
                $"No variant expected file for {name}");
        }
    }

    [Theory]
    [MemberData(nameof(VariantCases))]
    public void Fixture_CondensesToExpected(string name, string variant)
    {
        var input = File.ReadAllText(Path.Combine(PipelineFixtureDir, $"{name}.input.txt"));
        var expected = File.ReadAllText(Path.Combine(PipelineFixtureDir, $"{name}.{variant}.expected.txt"));
        var ids = Variants.Single(v => v.Suffix == variant).Ids;

        Assert.Equal(expected, RunPipeline(input, ids));
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("high")]
    public void Properties_PipelineIsIdempotentOverCorporaAndFixtures(string variant)
    {
        var ids = Variants.Single(v => v.Suffix == variant).Ids;
        var inputs = OutputMinifierTests.AdversarialInputs
            .Concat(OutputCondenserTests.AdversarialInputs())
            .Concat(Directory.GetFiles(GetFixtureDir(), "*.input.txt").Select(File.ReadAllText))
            .Concat(Directory.GetFiles(PipelineFixtureDir, "*.input.txt").Select(File.ReadAllText));

        foreach (var input in inputs)
        {
            var once = RunPipeline(input, ids);

            Assert.True(once == RunPipeline(once, ids),
                $"Pipeline not idempotent at {variant} for input of length {input.Length}");
            Assert.True(once == RunPipeline(input, ids),
                $"Pipeline not deterministic at {variant} for input of length {input.Length}");
            Assert.True(once.Length <= input.Length,
                $"Pipeline grew output at {variant}: {input.Length} → {once.Length}");
        }
    }
}
