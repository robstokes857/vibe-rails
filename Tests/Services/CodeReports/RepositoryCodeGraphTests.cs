using System.Diagnostics;
using System.Text.Json;
using MintLint;
using VibeRails.DTOs;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public sealed class RepositoryCodeGraphTests
{
    [Fact]
    public void SourceOutline_UsesParserTokens_AndDoesNotTreatCommentsOrStringsAsReferences()
    {
        var outline = SourceOutline.Read("Caller.cs", """
            // class Imaginary { Ghost ghost; }
            class Caller { Service value; string text = "Phantom"; void Run() {} }
            """);
        Assert.Contains(outline.Declarations, item => item.Name == "Caller" && item.Line == 2);
        Assert.Contains(outline.Declarations, item => item.Name == "Run");
        Assert.Contains(outline.References, item => item.Name == "Service");
        Assert.DoesNotContain(outline.References, item => item.Name is "Ghost" or "Phantom" or "Imaginary");
    }

    [Fact]
    public void SourceOutline_ReportsNoLanguage_WhenNoParserClaimsTheExtension_SoTheFieldIsOmitted()
    {
        var outline = SourceOutline.Read("notes.unmapped", "class Caller { Service value; }");
        Assert.Null(outline.Language);
        Assert.Empty(outline.Declarations);
        Assert.Empty(outline.References);

        var graph = RepositoryCodeGraph.Build("test", [("notes.unmapped", outline)], false,
            TestContext.Current.CancellationToken);
        var file = Assert.Single(graph.Nodes, node => node.Kind == "file");
        Assert.Null(file.Language);
        // An unclaimed extension has no language name, so the wire must omit the field rather
        // than publish an empty string the viewer would render as a language badge.
        Assert.DoesNotContain("\"language\"",
            JsonSerializer.Serialize(graph, AppJsonSerializerContext.Default.CodeGraphResponse));
    }

    [Fact]
    public void Build_ReturnsSnapshotCollections_SoTrimmingNeverRewritesAReturnedResponse()
    {
        var directory = string.Join('/', Enumerable.Repeat(new string('a', 240), 15));
        var trimmed = RepositoryCodeGraph.Build("large", Enumerable.Range(0, 1000).Select(index =>
            Source($"{directory}/File{index}.cs", $"class Type{index} {{ void Run() {{}} }}")).ToArray(),
            false, TestContext.Current.CancellationToken);
        var whole = RepositoryCodeGraph.Build("small", [Source("src/Api.cs", "class Api {}")], false,
            TestContext.Current.CancellationToken);

        Assert.True(trimmed.Truncated);
        Assert.True(trimmed.Nodes.Count < RepositoryCodeGraph.MaxNodes);
        foreach (var graph in new[] { trimmed, whole })
        {
            // The builder keeps shrinking its own lists while fitting the byte budget, so a
            // response that held them would describe a different map than the one it reports.
            Assert.False(graph.Nodes is ICollection<CodeGraphNode> { IsReadOnly: false });
            Assert.False(graph.Edges is ICollection<CodeGraphEdge> { IsReadOnly: false });
            Assert.Equal(graph.FileCount, graph.Nodes.Count(node => node.Kind == "file"));
        }
    }

    [Fact]
    public void Build_ConnectsDomainsWithEvidence_ResolvesLocalImports_AndSkipsAmbiguousTypeNames()
    {
        var files = new[] {
            Source("api/Caller.cs", "class Caller { Service value; Duplicate ignored; }"),
            Source("domain/Service.cs", "class Service {}"),
            Source("first/Duplicate.cs", "class Duplicate {}"),
            Source("second/Duplicate.cs", "class Duplicate {}"),
            Source("ui/view.js", "import { work } from '../core/task.js'; work();"),
            Source("core/task.js", "export function work() {}")
        };
        var graph = RepositoryCodeGraph.Build("test", files, false, TestContext.Current.CancellationToken);
        var caller = graph.Nodes.Single(node => node.Path == "api/Caller.cs");
        var service = graph.Nodes.Single(node => node.Path == "domain/Service.cs");
        Assert.Contains(graph.Edges, edge => edge.Source == caller.Id && edge.Target == service.Id
            && edge.Kind == "references" && edge.Evidence!.Contains("Service"));
        Assert.Contains(graph.Edges, edge => edge.Source == caller.ParentId && edge.Target == service.ParentId);
        Assert.DoesNotContain(graph.Edges, edge => edge.Evidence?.Contains("Duplicate") == true);
        Assert.Contains(graph.Edges, edge => edge.Evidence == "ui/view.js imports ../core/task.js");
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(node => node.Id).Distinct().Count());
        Assert.Equal(graph.Edges.Count, graph.Edges.Select(edge => edge.Id).Distinct().Count());
        Assert.All(graph.Edges, edge => {
            Assert.Contains(graph.Nodes, node => node.Id == edge.Source);
            Assert.Contains(graph.Nodes, node => node.Id == edge.Target);
        });
        Assert.Equal(graph.Nodes, RepositoryCodeGraph.Build("test", files, false, TestContext.Current.CancellationToken).Nodes);
    }

    [Fact]
    public void Build_BoundsSymbolsBeforeDroppingFileNodes()
    {
        var files = Enumerable.Range(0, 1000).Select(index =>
            Source($"src/F{index}.cs", $"class F{index} {{ void One() {{}} void Two() {{}} }}")).ToArray();
        var graph = RepositoryCodeGraph.Build("large", files, false, TestContext.Current.CancellationToken);
        Assert.True(graph.Truncated);
        Assert.Equal(1000, graph.Nodes.Count(node => node.Kind == "file"));
        Assert.True(graph.Nodes.Count <= 2800);
        Assert.True(graph.Edges.Count <= 10000);
    }

    [Fact]
    public void Build_BoundsReferencesIncludingDomainEdges_AndRejectsAboveRootImports()
    {
        var references = string.Join(' ', Enumerable.Range(0, 110).Select(index => $"Type{index}"));
        var files = Enumerable.Range(0, 110).Select(index =>
            Source($"domain{index}/file.cs", $"class Type{index} {{ {references} }}")).ToList();
        files.Add(Source("ui/view.js", "import '../../domain0/file.cs';"));
        var graph = RepositoryCodeGraph.Build("connected", files, false, TestContext.Current.CancellationToken);
        Assert.True(graph.Truncated);
        Assert.True(graph.Edges.Count <= 10000);
        Assert.DoesNotContain(graph.Edges, edge => edge.Evidence?.Contains("imports") == true);
    }

    [Fact]
    public void Build_BoundsLongReferenceEvidence_AndKeepsItsUsefulSuffix()
    {
        var directory = string.Join('/', Enumerable.Repeat(new string('a', 240), 14));
        var graph = RepositoryCodeGraph.Build("long-paths", [
            Source($"{directory}/Caller.cs", "class Caller { Service value; }"),
            Source("other/Service.cs", "class Service {}")
        ], false, TestContext.Current.CancellationToken);

        var references = graph.Edges.Where(edge => edge.Kind == "references").ToArray();
        Assert.NotEmpty(references);
        Assert.All(references, edge => Assert.True(edge.Evidence!.Length <= 512));
        Assert.Contains(references, edge => edge.Evidence!.EndsWith("mentions Service"));
        Assert.True(graph.Truncated);
    }

    [Fact]
    public void Build_FitsAtlasByteLimit_WhileRetainingFilesAndValidRelationships()
    {
        var directory = string.Join('/', Enumerable.Repeat(new string('a', 240), 15));
        var files = Enumerable.Range(0, 1000).Select(index =>
            Source($"{directory}/File{index}.cs", $"class Type{index} {{ void Run() {{}} }}")).ToArray();
        var graph = RepositoryCodeGraph.Build("large", files, false, TestContext.Current.CancellationToken);
        var wire = JsonSerializer.SerializeToUtf8Bytes(graph, AppJsonSerializerContext.Default.CodeGraphResponse);

        Assert.True(graph.Truncated);
        Assert.True(wire.Length <= RepositoryCodeGraph.MaxSerializedBytes);
        Assert.True(graph.Nodes.Count < RepositoryCodeGraph.MaxNodes);
        Assert.Equal(1000, graph.FileCount);
        Assert.Equal(graph.FileCount, graph.Nodes.Count(node => node.Kind == "file"));
        var ids = graph.Nodes.Select(node => node.Id).ToHashSet();
        Assert.All(graph.Nodes.Where(node => node.ParentId is not null), node => Assert.Contains(node.ParentId!, ids));
        Assert.All(graph.Edges, edge => {
            Assert.Contains(edge.Source, ids);
            Assert.Contains(edge.Target, ids);
        });
    }

    [Fact]
    public void Build_TrimsLongPathFilesOnlyAfterOptionalDetails_AreExhausted()
    {
        var shared = string.Join('/', Enumerable.Repeat(new string('a', 240), 14));
        var files = Enumerable.Range(0, 1000).Select(index => {
            var suffix = index.ToString("D4").PadRight(200, 'b');
            return ($"{shared}/{suffix}/{suffix}/File.cs", (SourceOutline?)null);
        }).ToArray();
        var graph = RepositoryCodeGraph.Build("long-paths", files, false, TestContext.Current.CancellationToken);
        var wire = JsonSerializer.SerializeToUtf8Bytes(graph, AppJsonSerializerContext.Default.CodeGraphResponse);

        Assert.True(graph.Truncated);
        Assert.True(wire.Length <= RepositoryCodeGraph.MaxSerializedBytes);
        Assert.InRange(graph.FileCount, 1, 999);
        Assert.Equal(graph.FileCount, graph.Nodes.Count(node => node.Kind == "file"));
        var ids = graph.Nodes.Select(node => node.Id).ToHashSet();
        Assert.All(graph.Nodes.Where(node => node.ParentId is not null), node => Assert.Contains(node.ParentId!, ids));
        Assert.All(graph.Edges, edge => {
            Assert.Contains(edge.Source, ids);
            Assert.Contains(edge.Target, ids);
        });
    }

    [Fact]
    public void RepositoryName_UsesFilesystemRootWhenItHasNoFinalComponent()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(root, RepositoryCodeGraph.RepositoryName(root));
        Assert.Equal("project", RepositoryCodeGraph.RepositoryName(Path.Combine(root, "project")));
    }

    [Fact]
    public void Build_PreservesDirectoryAncestry_ForProgressiveOverviews()
    {
        var graph = RepositoryCodeGraph.Build("project", [Source("src/api/Handler.cs", "class Handler {}")], false,
            TestContext.Current.CancellationToken);
        var root = Assert.Single(graph.Nodes, node => node.ParentId is null);
        Assert.Equal("src", root.Name);
        var directory = Assert.Single(graph.Nodes, node => node.ParentId == root.Id);
        Assert.Equal("src/api", directory.Path);
        Assert.Equal("api", directory.Name);
        Assert.Contains(graph.Nodes, node => node.Kind == "file" && node.ParentId == directory.Id);
    }

    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("/etc/secret.cs")]
    [InlineData("C:/secret.cs")]
    [InlineData("src/../secret.cs")]
    [InlineData("src\\secret.cs")]
    [InlineData("src/file.cs:stream")]
    [InlineData("src/file\n.cs")]
    public void RejectsNonRepositoryPaths(string path) => Assert.False(RepositoryCodeGraph.IsSafePath(path));

    [Fact]
    public async Task ReadAsync_UsesCurrentGitFiles_PrioritizesReport_AndHonorsReadBounds()
    {
        var root = Path.Combine(Path.GetTempPath(), "viberails-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored.cs\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.cs"), "class Hidden {}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.cs"), "class Before {}", TestContext.Current.CancellationToken);
            await Git(root, "add", "tracked.cs");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.cs"), "class Current {}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "report.js"), "export class Report {}", TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "vendor"));
            await File.WriteAllTextAsync(Path.Combine(root, "vendor", "measured.js"), "export class Measured {}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "large.cs"), new string('x', 140 * 1024), TestContext.Current.CancellationToken);
            var graph = await new RepositoryCodeGraph().ReadAsync(root, ["report.js", "vendor/measured.js"], TestContext.Current.CancellationToken);
            Assert.Equal("report.js", graph.Nodes.First(node => node.Kind == "file").Path);
            Assert.Contains(graph.Nodes, node => node.Name == "Current");
            Assert.DoesNotContain(graph.Nodes, node => node.Name is "Before" or "Hidden");
            Assert.True(graph.Truncated);
            Assert.Contains(graph.Nodes, node => node.Path == "large.cs" && node.Kind == "file");
            Assert.Contains(graph.Nodes, node => node.Path == "vendor/measured.js" && node.Kind == "file");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotFollowDirectoryLinks_EvenForPrioritizedTrackedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "viberails-graph-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "viberails-graph-outside-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "linked");
        Directory.CreateDirectory(link);
        Directory.CreateDirectory(outside);
        try
        {
            await Git(root, "init");
            await File.WriteAllTextAsync(Path.Combine(link, "secret.cs"), "class Original {}", TestContext.Current.CancellationToken);
            await Git(root, "add", "linked/secret.cs");
            File.Delete(Path.Combine(link, "secret.cs"));
            Directory.Delete(link);
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.cs"), "class PrivateOutsideRepository {}", TestContext.Current.CancellationToken);
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe") {
                    ArgumentList = { "/c", "mklink", "/J", link, outside },
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                });
                await process!.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(link, outside);
            var graph = await new RepositoryCodeGraph().ReadAsync(root, ["linked/secret.cs"], TestContext.Current.CancellationToken);
            Assert.Empty(graph.Nodes);
            // The guard dropped the only catalogued file, so the map is partial and must say so.
            Assert.True(graph.Truncated);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    private static (string, SourceOutline?) Source(string path, string text) => (path, SourceOutline.Read(path, text));

    private static async Task Git(string root, params string[] args)
    {
        using var process = new Process { StartInfo = new("git") {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }
}
