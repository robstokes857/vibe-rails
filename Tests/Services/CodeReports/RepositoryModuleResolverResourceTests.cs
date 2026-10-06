using System.Diagnostics;
using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public sealed class RepositoryModuleResolverResourceTests
{
    [Fact]
    public void PythonRoots_WithDeeplyNestedSrcSegmentsAndPackages_AreComputedInLinearSpace()
    {
        // Every `src` segment used to re-join every prefix below it, and every package walked
        // its ancestry with fresh substrings: cubic and quadratic in path depth respectively.
        var deepSource = string.Concat(Enumerable.Repeat("src/", 900)) + "app.py";
        var files = new List<(string Path, SourceOutline? Outline)> { (deepSource, SourceOutline.Read(deepSource, "import os")) };
        for (var depth = 1; depth <= 400; depth++)
        {
            var path = string.Join('/', Enumerable.Repeat("pkg", depth)) + "/__init__.py";
            files.Add((path, SourceOutline.Read(path, "")));
        }
        _ = RepositoryCodeGraph.Build("warmup", [("warm.py", SourceOutline.Read("warm.py", "import os"))], false,
            TestContext.Current.CancellationToken);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var graph = RepositoryCodeGraph.Build("resource", files, false, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 24L * 1024 * 1024, $"Deep Python roots allocated {allocated:N0} bytes.");
        Assert.Equal(401, graph.FileCount);
        Assert.DoesNotContain(graph.Diagnostics!.Omissions, omission => omission.Code == "module-work-limit");
        GC.KeepAlive(graph);
    }

    [Fact]
    public void PythonImports_AcrossManyRootsWithTheSameEntry_StopAtTheWorkBudgetAndReportIt()
    {
        // Each unresolved import used to scan every root. Here 500 roots all hold a `z` directory
        // that is not a module, so every probe is real work that finds nothing; the budget must
        // cut it instead of running 4.5 million probes to completion.
        var files = new List<(string Path, SourceOutline? Outline)>();
        for (var index = 0; index < 500; index++)
        {
            var path = $"r{index}/src/z/other.py";
            files.Add((path, SourceOutline.Read(path, "")));
        }
        var importer = string.Concat(Enumerable.Repeat("import z\n", 9_000));
        files.Add(("app/main.py", SourceOutline.Read("app/main.py", importer)));

        var stopwatch = Stopwatch.StartNew();
        var graph = RepositoryCodeGraph.Build("resource", files, false, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Building took {stopwatch.Elapsed}.");
        Assert.True(graph.Truncated);
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Code == "module-work-limit" && omission.Count == 1);
        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == "references");
    }

    [Fact]
    public void PythonImports_OfExternalModules_ProbeNoRootsAndResolveUniqueCrossRootModules()
    {
        var files = new List<(string Path, SourceOutline? Outline)>();
        for (var index = 0; index < 300; index++)
        {
            var path = $"r{index}/src/local{index}.py";
            files.Add((path, SourceOutline.Read(path, "")));
        }
        files.Add(("shared/src/util.py", SourceOutline.Read("shared/src/util.py", "")));
        var importer = string.Concat(Enumerable.Range(0, 3_000).Select(index => $"import ext{index}\n")) + "import util\n";
        files.Add(("app/main.py", SourceOutline.Read("app/main.py", importer)));

        var graph = RepositoryCodeGraph.Build("resource", files, false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(graph.Diagnostics!.Omissions, omission => omission.Code == "module-work-limit");
        var util = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "shared/src/util.py");
        var main = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "app/main.py");
        Assert.Contains(graph.Edges, edge => edge.Kind == "references" && edge.Source == main.Id && edge.Target == util.Id);
    }

    [Fact]
    public void RustCrateMaps_RevisitedFromManyRoots_StopAtTheWorkBudgetAndReportIt()
    {
        // A main.rs beside every mod.rs makes each file reachable from every root above it, and
        // every visit re-reads all of the file's mod declarations against a long module path.
        var files = new List<(string Path, SourceOutline? Outline)>();
        var declarations = string.Concat(Enumerable.Range(0, 200).Select(index => $"mod x{index};\n"));
        for (var depth = 0; depth <= 300; depth++)
        {
            var directory = string.Join('/', Enumerable.Range(1, depth).Select(level => $"d{level}"));
            var body = $"mod d{depth + 1};\n" + declarations;
            var main = directory.Length == 0 ? "main.rs" : directory + "/main.rs";
            files.Add((main, SourceOutline.Read(main, body)));
            if (depth > 0) files.Add((directory + "/mod.rs", SourceOutline.Read(directory + "/mod.rs", body)));
        }
        _ = RepositoryCodeGraph.Build("warmup", [("warm.rs", SourceOutline.Read("warm.rs", "mod a;"))], false,
            TestContext.Current.CancellationToken);

        // Allocation, not wall-clock: the budget is a deterministic unit count, and a 10 s stopwatch
        // bound tripped once under a fully parallel suite while the build took under a second alone
        // (VIBE-109). The 4M-unit cut lands at about 1.4 GB of module-path strings; a looser budget
        // or a lost cut grows that proportionally, so the bound is not far above the measured value.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var graph = RepositoryCodeGraph.Build("resource", files, false, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 2L * 1024 * 1024 * 1024, $"Budget-cut crate maps allocated {allocated:N0} bytes.");
        Assert.True(graph.Truncated);
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Code == "module-work-limit" && omission.Count > 0);
        // Roots are walked shallowest first, so the top-level crate map completes before the cut
        // and still yields its real edges.
        var root = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "main.rs");
        var first = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "d1/mod.rs");
        Assert.Contains(graph.Edges, edge => edge.Kind == "references" && edge.Source == root.Id && edge.Target == first.Id);
    }

    [Fact]
    public void RustWorkspace_OfOrdinarySize_ResolvesWithoutReachingTheBudget()
    {
        var files = new List<(string Path, SourceOutline? Outline)> { ("Cargo.toml", null) };
        for (var crate = 0; crate < 40; crate++)
        {
            var modules = string.Concat(Enumerable.Range(0, 30).Select(index => $"mod m{index};\n"));
            files.Add(($"crates/c{crate}/Cargo.toml", null));
            files.Add(($"crates/c{crate}/src/lib.rs", SourceOutline.Read("lib.rs", modules)));
            for (var module = 0; module < 30; module++)
            {
                var path = $"crates/c{crate}/src/m{module}.rs";
                files.Add((path, SourceOutline.Read(path, $"use crate::m{(module + 1) % 30}::Thing;\nuse super::m0;")));
            }
        }

        var graph = RepositoryCodeGraph.Build("resource", files, false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(graph.Diagnostics!.Omissions, omission => omission.Code == "module-work-limit");
        var lib = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "crates/c7/src/lib.rs");
        var m3 = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "crates/c7/src/m3.rs");
        var m4 = Assert.Single(graph.Nodes, node => node.Kind == "file" && node.Path == "crates/c7/src/m4.rs");
        Assert.Contains(graph.Edges, edge => edge.Kind == "references" && edge.Source == lib.Id && edge.Target == m3.Id);
        Assert.Contains(graph.Edges, edge => edge.Kind == "references" && edge.Source == m3.Id && edge.Target == m4.Id);
    }

    [Fact]
    public void CargoOwner_WithManyManifestsInTheCatalog_DoesNotScanThemPerModule()
    {
        var catalog = Enumerable.Range(0, 20_000).Select(index => $"vendor/v{index}/Cargo.toml").Append("app/Cargo.toml").ToArray();
        var files = new List<(string Path, SourceOutline? Outline)>();
        var declarations = string.Concat(Enumerable.Range(0, 400).Select(index => $"mod s{index};\n"));
        for (var index = 0; index < 400; index++)
        {
            var path = $"app/src/s{index}.rs";
            files.Add((path, SourceOutline.Read(path, declarations)));
        }

        var stopwatch = Stopwatch.StartNew();
        var graph = RepositoryCodeGraph.Build("resource", files, false, TestContext.Current.CancellationToken, catalogPaths: catalog);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Building took {stopwatch.Elapsed}.");
        Assert.DoesNotContain(graph.Diagnostics!.Omissions, omission => omission.Code == "module-work-limit");
    }
}
