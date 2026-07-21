using MintLint;
using Xunit;

namespace Tests.MintLintTests;

public sealed class ImpactAnalyzerTests : IDisposable
{
    private readonly string _root;

    public ImpactAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mintlint-impact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void CountReferencingFiles_CountsDistinctFilesUsingDeclaredNames()
    {
        Write("src/alpha.cs", "public class AlphaService { public void RunAlpha() { } }");
        Write("src/uses_alpha.cs", "public class Caller { void Go() { new AlphaService().RunAlpha(); } }");
        Write("src/uses_alpha_twice.cs", "public class Other { AlphaService a; AlphaService b; }");
        Write("src/unrelated.cs", "public class Nothing { void Go() { } }");
        // Same word embedded in a longer identifier must not count.
        Write("src/near_miss.cs", "public class Fake { void Go() { var x = nameof(AlphaServiceFactory); } }");

        var targets = MintLintAnalyzer.AnalyzePath(_root);
        var counts = ImpactAnalyzer.CountReferencingFiles(_root, targets);

        Assert.Equal(2, counts["src/alpha.cs"]);
    }

    [Fact]
    public void CountReferencingFiles_IgnoresSyntheticAndTinyNames()
    {
        Write("src/tiny.cs", "public class Ab { void Go() { } }");
        Write("src/other.cs", "public class Uses { Ab field; }");

        var targets = MintLintAnalyzer.AnalyzePath(_root);
        var counts = ImpactAnalyzer.CountReferencingFiles(_root, targets);

        // "Ab" is below the minimum name length; "Go" too. Nothing to match on.
        Assert.Equal(0, counts["src/tiny.cs"]);
    }

    [Fact]
    public void CountReferencingFiles_IgnoresCandidatesOutsideRoot()
    {
        Write("src/alpha.cs", "public class AlphaService { }");
        string outsidePath = Path.Combine(Path.GetDirectoryName(_root)!, $"outside-{Guid.NewGuid():N}.cs");
        File.WriteAllText(outsidePath, "public class Outside { AlphaService field; }");

        try
        {
            var targets = MintLintAnalyzer.AnalyzePath(_root);
            var candidates = new[] { Path.GetRelativePath(_root, outsidePath) };

            var counts = ImpactAnalyzer.CountReferencingFiles(_root, targets, candidates);

            Assert.Equal(0, counts["src/alpha.cs"]);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void CountReferencingFiles_CountsDuplicateCandidateOnlyOnce()
    {
        Write("src/alpha.cs", "public class AlphaService { }");
        Write("src/uses_alpha.cs", "public class Caller { AlphaService field; }");
        var targets = MintLintAnalyzer.AnalyzePath(_root);

        var counts = ImpactAnalyzer.CountReferencingFiles(
            _root,
            targets,
            ["src/uses_alpha.cs", "src/uses_alpha.cs"]);

        Assert.Equal(1, counts["src/alpha.cs"]);
    }

    [Fact]
    public void CountReferencingSources_UsesCapturedContentInsteadOfFilesOnDisk()
    {
        Write("src/alpha.cs", "public class AlphaService { }");
        Write("src/caller.cs", "public class DiskCaller { }");
        var targets = MintLintAnalyzer.AnalyzePath(_root);

        var counts = ImpactAnalyzer.CountReferencingSources(
            targets,
            [
                new SourceInput("src/alpha.cs", "public class AlphaService { }"),
                new SourceInput("src/caller.cs", "public class HeadCaller { AlphaService service; }")
            ]);

        Assert.Equal(1, counts["src/alpha.cs"]);
    }

    private void Write(string relativePath, string content)
    {
        string fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
