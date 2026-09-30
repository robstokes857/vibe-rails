using System.Text;
using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public sealed class RepositoryReferenceResourceTests
{
    [Fact]
    public void ReadAndBuild_DoNotExpandEveryUsingForUnrelatedFieldIdentifiers()
    {
        // The review repro fits comfortably inside the 128 KiB source-read limit but previously
        // allocated about one GiB by expanding 2,000 imports for each type and field identifier.
        var content = Imports(2000) + "class Caller {\n"
            + string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"T{index} f{index};")) + "\n}";
        Assert.True(Encoding.UTF8.GetByteCount(content) < 128 * 1024);

        // Warm static parser/serializer initialization outside the measured section. Current-thread
        // allocation is deterministic for this synchronous path and independent of parallel tests.
        _ = RepositoryCodeGraph.Build("warmup", [("Warmup.cs", SourceOutline.Read("Warmup.cs", "class Warmup {}"))], false,
            TestContext.Current.CancellationToken);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outline = SourceOutline.Read("Caller.cs", content);
        var graph = RepositoryCodeGraph.Build("resource", [("Caller.cs", outline)], false,
            TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32L * 1024 * 1024, $"A small source and graph allocated {allocated:N0} bytes.");
        Assert.False(outline.ReferenceScope!.Truncated);
        Assert.False(graph.Truncated);
        Assert.Equal(1, graph.FileCount);
        Assert.Contains(graph.Nodes, node => node.Kind == "class" && node.Name == "Caller");
        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == "references");
        GC.KeepAlive(outline);
        GC.KeepAlive(graph);
    }

    [Fact]
    public void Build_ReportsWhenMatchingNamesExhaustReferenceWork_AndKeepsFileStructure()
    {
        // Unlike the allocation repro, every identifier here names a real type. Name-index pruning
        // cannot bypass the work limit: each name has 2,000 possible imported namespace prefixes.
        var caller = Imports(2000) + "class Caller {\n"
            + string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"Type{index} f{index};")) + "\n}";
        var declarations = "namespace N1999;\n"
            + string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"class Type{index} {{}}"));
        var graph = RepositoryCodeGraph.Build("resource", [
            ("Caller.cs", SourceOutline.Read("Caller.cs", caller)),
            ("Types.cs", SourceOutline.Read("Types.cs", declarations))
        ], false, TestContext.Current.CancellationToken);

        Assert.Equal(2, graph.FileCount);
        Assert.True(graph.Truncated);
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Code == "reference-work-limit" && omission.Count > 0);
        var fileIds = graph.Nodes.Where(node => node.Kind == "file").Select(node => node.Id).ToHashSet();
        Assert.All(graph.Edges.Where(edge => edge.Kind == "references"), edge =>
        {
            Assert.Contains(edge.Source, fileIds);
            Assert.Contains(edge.Target, fileIds);
        });
    }

    [Fact]
    public void Build_DoesNotPublishAPartialMatch_WhenTheWorkLimitHidesAnotherCandidate()
    {
        var outline = new SourceOutline("CSharp", [], [], [])
        {
            ReferenceScope = new SourceReferenceScope([], [
                new SourceScopedReference("Shared", 1, Candidates(), "in imported namespaces", "Shared")
            ])
        };
        var graph = RepositoryCodeGraph.Build("resource", [
            ("Caller.cs", outline),
            ("Left.cs", SourceOutline.Read("Left.cs", "namespace Left; class Shared {}")),
            ("Right.cs", SourceOutline.Read("Right.cs", "namespace Right; class Shared {}"))
        ], false, TestContext.Current.CancellationToken);

        Assert.True(graph.Truncated);
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Code == "reference-work-limit" && omission.Count > 0);
        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == "references");

        static IEnumerable<string> Candidates()
        {
            yield return "Left.Shared";
            for (var index = 0; index <= RepositoryReferenceResolver.MaxCandidatesPerFile; index++) yield return $"Missing{index}.Shared";
            yield return "Right.Shared";
        }
    }

    [Fact]
    public void Build_ReportsBoundedNestedNamespaceEvidence_WhileKeepingTheDeclaration()
    {
        var source = string.Concat(Enumerable.Repeat("namespace LongNamespaceName {\n", 300))
            + "class Leaf {}\n" + new string('}', 300);
        var outline = SourceOutline.Read("Deep.cs", source);
        var graph = RepositoryCodeGraph.Build("resource", [("Deep.cs", outline)], false,
            TestContext.Current.CancellationToken);

        Assert.True(outline.ReferenceScope!.Truncated);
        Assert.True(graph.Truncated);
        Assert.Equal(1, graph.FileCount);
        Assert.Contains(graph.Nodes, node => node.Kind == "class" && node.Name == "Leaf");
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Count > 0 && omission.Code == "reference-scope-limit");
    }

    [Fact]
    public void ReadAndBuild_BoundsRepeatedQualifiedNamePrefixes()
    {
        var source = "class Caller { void Run() { " + string.Join('.', Enumerable.Repeat("Part", 3000)) + ".Run(); } }";
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outline = SourceOutline.Read("Chain.cs", source);
        var graph = RepositoryCodeGraph.Build("resource", [("Chain.cs", outline)], false,
            TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32L * 1024 * 1024, $"A bounded qualified-name outline allocated {allocated:N0} bytes.");
        Assert.True(outline.ReferenceScope!.Truncated);
        Assert.True(graph.Truncated);
        Assert.Equal(1, graph.FileCount);
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Code == "reference-scope-limit" && omission.Count > 0);
        GC.KeepAlive(outline);
        GC.KeepAlive(graph);
    }

    private static string Imports(int count) => string.Join('\n', Enumerable.Range(0, count).Select(index => $"using N{index};")) + "\n";

    [Fact]
    public void Read_DoesNotRescanTheFile_ForEveryUnterminatedUsing()
    {
        // 120 KiB of `using` keywords with no statement end: each keyword used to scan to the end
        // of the file before the next keyword repeated the same walk.
        var source = string.Concat(Enumerable.Repeat("using ", 20_000));
        Assert.True(Encoding.UTF8.GetByteCount(source) < 128 * 1024);
        _ = SourceOutline.Read("warm.cs", "using System; class W {}");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outline = SourceOutline.Read("unterminated.cs", source);
        stopwatch.Stop();

        // A timing budget on linear work: the guarded read is ~9 ms for this input (measured
        // 2026-09-29), so even heavy parallel starvation stays far below 1 s. A reintroduced
        // per-keyword rescan is quadratic and takes ~1.3 s unloaded (4-6 s in parallel runs),
        // so it fails this budget deterministically, not only under load.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Reading took {stopwatch.Elapsed}.");
        Assert.False(outline.ReferenceScope!.Truncated);
    }

    [Fact]
    public void Read_KeepsDeclarationsReadBeforeTheDeclarationTextBudget()
    {
        // A file whose class names exhaust the declaration text budget still contributes the
        // types it had already qualified; only its own references are withheld.
        // A long namespace is repeated in every qualified name, so 25 KiB of source needs
        // more than the 256 Ki-character declaration budget.
        var source = "namespace " + new string('N', 200) + ";\n"
            + string.Concat(Enumerable.Range(0, 2000).Select(index => $"class C{index} {{}}\n"));
        Assert.True(Encoding.UTF8.GetByteCount(source) < 128 * 1024);
        var outline = SourceOutline.Read("Many.cs", source);

        Assert.True(outline.ReferenceScope!.Truncated);
        Assert.NotEmpty(outline.ReferenceScope.Declarations);
        Assert.Empty(outline.ReferenceScope.References);
    }

    [Fact]
    public void Read_BoundsRepeatedCopiesOfAnAlreadySeenQualifiedChain()
    {
        var chain = string.Join('.', Enumerable.Repeat("Part", 120)) + ".Run();\n";
        var source = "class Caller { void Run() { " + string.Concat(Enumerable.Repeat(chain, 200)) + "} }";
        Assert.True(Encoding.UTF8.GetByteCount(source) < 128 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outline = SourceOutline.Read("Repeated.cs", source);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32L * 1024 * 1024, $"Repeated chains allocated {allocated:N0} bytes.");
        Assert.True(outline.ReferenceScope!.Truncated);
        GC.KeepAlive(outline);
    }
}
