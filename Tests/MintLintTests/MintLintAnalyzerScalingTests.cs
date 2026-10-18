using System.Diagnostics;
using System.Text;
using MintLint;
using Xunit;

namespace Tests.MintLintTests;

public sealed class MintLintAnalyzerScalingTests
{
    [Fact]
    public void ArrowHeavyTypeScript_IsAnalyzedInLinearTime()
    {
        // About 1 MiB: inside the pre-commit hook's 5 MiB staged-file limit. Every arrow is a
        // function, and each metric pass used to scan every function for every token, so this
        // took about 50 seconds of CPU before nested-function lookups were indexed by start token.
        const int lineCount = 12_500;
        var source = string.Concat(Enumerable.Range(0, lineCount).Select(index =>
            $"export const f{index} = (a) => {{ const g = (b) => (c) => a + b + c; return g(a)(a); }};\n"));
        var bytes = Encoding.UTF8.GetByteCount(source);
        Assert.InRange(bytes, 1024 * 1024, 5 * 1024 * 1024);

        _ = MintLintAnalyzer.AnalyzeSources([new SourceInput("warm.ts", "const f = (a) => (b) => a + b;\n")]);
        var stopwatch = Stopwatch.StartNew();
        var metrics = MintLintAnalyzer.AnalyzeSources([new SourceInput("bundle.ts", source)]);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Analysis took {stopwatch.Elapsed}.");
        var file = Assert.Single(metrics);
        // Three arrows per line plus the synthetic global scope.
        Assert.Equal(3 * lineCount + 1, file.Functions.Count);
    }
}
