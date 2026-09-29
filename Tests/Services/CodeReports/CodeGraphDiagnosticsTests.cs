using System.Diagnostics;
using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public sealed class CodeGraphDiagnosticsTests
{
    [Fact]
    public void Build_CountsDeclarationCapsSeparatelyFromFileCoverage()
    {
        var source = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"class Type{i} {{}}"));
        var graph = RepositoryCodeGraph.Build("test", [("types.cs", SourceOutline.Read("types.cs", source))], false,
            TestContext.Current.CancellationToken);
        Assert.True(graph.Truncated);
        Assert.Equal(1, graph.FileCount);
        var omission = Assert.Single(graph.Diagnostics!.Omissions);
        Assert.Equal("declaration-limit", omission.Code);
        Assert.Equal(8, omission.Count);
    }

    [Fact]
    public void Build_CountsDeclarationsThatGiveWayToFileStructure()
    {
        var files = Enumerable.Range(0, 2000).Select(i => ($"file{i}.cs", (SourceOutline?)SourceOutline.Read($"file{i}.cs", $"class Type{i} {{}}"))).ToArray();
        var graph = RepositoryCodeGraph.Build("test", files, false, TestContext.Current.CancellationToken);
        Assert.Equal(2000, graph.FileCount);
        Assert.Equal(2800, graph.Nodes.Count);
        Assert.Equal(1201, Assert.Single(graph.Diagnostics!.Omissions, item => item.Code == "declaration-node-limit").Count);
    }

    [Fact]
    public void Build_CountsOmittedFileConnectionsOnceForMultipleTypeMentions()
    {
        var references = string.Join(' ', Enumerable.Range(0, 110).Select(i => $"First{i} Second{i}"));
        var files = Enumerable.Range(0, 110).Select(i => ($"area{i}/types.cs",
            (SourceOutline?)SourceOutline.Read($"area{i}/types.cs", $"class First{i} {{ {references} }} class Second{i} {{}}"))).ToArray();
        var graph = RepositoryCodeGraph.Build("test", files, false, TestContext.Current.CancellationToken);
        var fileIds = graph.Nodes.Where(node => node.Kind == "file").Select(node => node.Id).ToHashSet();
        var kept = graph.Edges.Count(edge => edge.Kind == "references" && fileIds.Contains(edge.Source));
        var omitted = Assert.Single(graph.Diagnostics!.Omissions, item => item.Code == "edge-limit").Count;
        Assert.Equal(110 * 109, kept + omitted);
    }

    [Fact]
    public async Task Read_CountsUnreadOutlinesAndFilters_AndExplicitlyIncludesDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "viberails-graph-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git("init", "--quiet");
            foreach (var path in new[] { "source.cs", "assets/library.js", "vendor/lib.py", "node_modules/pkg/main.js", "obj/Generated.cs" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
                await File.WriteAllTextAsync(Path.Combine(root, path), "// source", TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "large.rs"), new string('x', 128 * 1024 + 1), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "binary.cs"), "class Example {}\0", TestContext.Current.CancellationToken);
            await Git("add", "--force", "--all");

            var graph = await new RepositoryCodeGraph().ReadAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(3, graph.FileCount);
            Assert.Equal(7, graph.Diagnostics!.SupportedFiles);
            Assert.Equal(3, graph.Diagnostics.ExcludedDependencyFiles);
            Assert.Equal(1, graph.Diagnostics.ExcludedBuildOutputFiles);
            Assert.Equal(1, Assert.Single(graph.Diagnostics.Omissions, item => item.Code == "file-size").Count);
            Assert.Equal(1, Assert.Single(graph.Diagnostics.Omissions, item => item.Code == "binary-source").Count);
            Assert.DoesNotContain(graph.Nodes, node => node.Path == "assets/library.js");
            Assert.Contains(graph.Nodes, node => node.Path == "large.rs");

            var included = await new RepositoryCodeGraph().ReadAsync(root, [], TestContext.Current.CancellationToken, includeDependencies: true);
            Assert.Equal(6, included.FileCount);
            Assert.Contains(included.Nodes, node => node.Path == "assets/library.js");
            Assert.Equal(0, included.Diagnostics!.ExcludedDependencyFiles);
            Assert.True(included.Diagnostics.IncludesDependencies);
            Assert.DoesNotContain(included.Nodes, node => node.Path == "obj/Generated.cs");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        async Task Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
    }
}
