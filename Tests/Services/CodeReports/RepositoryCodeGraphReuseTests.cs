using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

/// <summary>
/// The Quality page asks for the map on every open and every scan (VIBE-313). An unchanged tree
/// answers with the capture it already has; anything a capture depends on makes a new one.
/// </summary>
public sealed class RepositoryCodeGraphReuseTests
{
    private static readonly DateTime Settled = DateTime.UtcNow.AddHours(-1);

    [Fact]
    public async Task ReadJsonAsync_ReusesTheCapture_UntilAFileOrTheCatalogChanges()
    {
        var root = await CreateRepository();
        try
        {
            await Write(root, "alpha.cs", "class Alpha {}", Settled);
            await Write(root, "bravo.cs", "class Bravo {}", Settled);
            await Commit(root);
            var graph = new RepositoryCodeGraph();

            var first = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            var second = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            // The same bytes, not an equal rebuild: the page tells an unchanged map by its capture time.
            Assert.True(SameBuffer(first, second));
            Assert.Equal(new[] { "Alpha", "Bravo" }, Declarations(first));

            // Same length, later modified time: the outline can differ, so the capture is not reused.
            await Write(root, "bravo.cs", "class Delta {}", Settled.AddMinutes(1));
            var edited = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "Alpha", "Delta" }, Declarations(edited));
            Assert.True(SameBuffer(edited, await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken)));

            // A new file changes Git's catalog even though no mapped file changed.
            await Write(root, "charlie.cs", "class Charlie {}", Settled);
            var added = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "Alpha", "Charlie", "Delta" }, Declarations(added));
            Assert.True(SameBuffer(added, await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken)));

            // A deleted file leaves the catalog: its nodes go with it.
            File.Delete(Path.Combine(root, "charlie.cs"));
            Assert.Equal(new[] { "Alpha", "Delta" },
                Declarations(await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken)));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadJsonAsync_PutsReportPrioritiesAndChangedFilesFirst_AndRemembersBothRequests()
    {
        var root = await CreateRepository();
        try
        {
            await Write(root, "a/one.cs", "class One {}", Settled);
            await Write(root, "b/two.cs", "class Two {}", Settled);
            await Write(root, "c/three.cs", "class Three {}", Settled);
            await Commit(root);
            var graph = new RepositoryCodeGraph();

            var plain = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "a/one.cs", "b/two.cs", "c/three.cs" }, Files(plain));

            // Priorities select and order the files, so they are part of what a capture is.
            var prioritized = await graph.ReadJsonAsync(root, ["c/three.cs"], TestContext.Current.CancellationToken);
            Assert.False(SameBuffer(plain, prioritized));
            Assert.Equal(new[] { "c/three.cs", "a/one.cs", "b/two.cs" }, Files(prioritized));
            // The page asks without priorities, then with the report's files: both stay remembered.
            Assert.True(SameBuffer(plain, await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken)));
            Assert.True(SameBuffer(prioritized, await graph.ReadJsonAsync(root, ["c/three.cs"], TestContext.Current.CancellationToken)));

            // The page's first request names no file, because the scan has not answered yet. A file the
            // working tree changed is what that scan will report, so it leads the selection anyway.
            await Write(root, "b/two.cs", "class Six {}", Settled.AddMinutes(1));
            await Write(root, "c/new.cs", "class New {}", Settled);
            var changed = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "b/two.cs", "c/new.cs", "a/one.cs", "c/three.cs" }, Files(changed));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadJsonAsync_ReusesTheCapture_WhenPrioritiesSelectTheSameFiles()
    {
        var root = await CreateRepository();
        try
        {
            await Write(root, "a/one.cs", "class One {}", Settled);
            await Write(root, "b/two.cs", "class Two {}", Settled);
            await Write(root, "vendor/lib/bundle.js", "export function bundled() {}", Settled);
            await Write(root, "notes.txt", "not source", Settled);
            await Commit(root);
            var graph = new RepositoryCodeGraph();
            var plain = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "a/one.cs", "b/two.cs" }, Files(plain));

            // A scan reports files the map never holds (a vendored bundle, a deleted file, a text
            // file). The page asks for them once; that request must not cost a second capture, or
            // the map on screen is torn down and rebuilt for nothing.
            Assert.True(SameBuffer(plain, await graph.ReadJsonAsync(root,
                ["vendor/lib/bundle.js", "notes.txt", "gone/missing.cs"], TestContext.Current.CancellationToken)));
            // So is a priority that leaves the selection in the order it already had.
            Assert.True(SameBuffer(plain, await graph.ReadJsonAsync(root, ["a/one.cs"], TestContext.Current.CancellationToken)));
            // One that moves a file is a different map.
            var moved = await graph.ReadJsonAsync(root, ["b/two.cs"], TestContext.Current.CancellationToken);
            Assert.False(SameBuffer(plain, moved));
            Assert.Equal(new[] { "b/two.cs", "a/one.cs" }, Files(moved));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void WithChangedPaths_AddsChangedSourceFiles_WithinThePriorityLimit()
    {
        Assert.Equal(new[] { "report.cs", "src/app.js" },
            RepositoryCodeGraph.WithChangedPaths(["report.cs"], ["src/app.js", "report.cs", "src/app.js", "notes.txt", "node_modules/pkg/index.js", "../outside.cs"]));
        var explicitOnly = new[] { "report.cs" };
        Assert.Same(explicitOnly, RepositoryCodeGraph.WithChangedPaths(explicitOnly, []));
        Assert.Same(explicitOnly, RepositoryCodeGraph.WithChangedPaths(explicitOnly, ["README.md"]));
        // More changes than fit beside the report's files: none is preferred, so the per-directory
        // turns still decide which files a limited map keeps.
        var many = Enumerable.Range(0, RepositoryCodeGraph.MaxPriorityFiles).Select(index => $"src/file{index}.cs").ToArray();
        Assert.Same(explicitOnly, RepositoryCodeGraph.WithChangedPaths(explicitOnly, many));
        Assert.Equal(RepositoryCodeGraph.MaxPriorityFiles, RepositoryCodeGraph.WithChangedPaths([], many).Count);
    }

    [Fact]
    public async Task ReadJsonAsync_NeverKeepsACaptureOfAFileThatWasJustWritten()
    {
        var root = await CreateRepository();
        try
        {
            // Written now: a second write within the same timestamp tick, at the same length, would
            // leave the file's metadata exactly as the capture saw it.
            var path = Path.Combine(root, "live.cs");
            await File.WriteAllTextAsync(path, "class Alpha {}", TestContext.Current.CancellationToken);
            var written = File.GetLastWriteTimeUtc(path);
            var graph = new RepositoryCodeGraph();
            var first = await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "Alpha" }, Declarations(first));

            await File.WriteAllTextAsync(path, "class Bravo {}", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, written);
            Assert.Equal(new[] { "Bravo" },
                Declarations(await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken)));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadJsonAsync_MatchesTheUncachedRead_OnTheWire()
    {
        var root = await CreateRepository();
        try
        {
            await Write(root, "src/app.js", "import { helper } from './helper.js';\nexport function run() { return helper(); }", Settled);
            await Write(root, "src/helper.js", "export function helper() { return 1; }", Settled);
            var graph = new RepositoryCodeGraph();
            var cached = JsonSerializer.Deserialize(
                (await graph.ReadJsonAsync(root, [], TestContext.Current.CancellationToken)).Span,
                AppJsonSerializerContext.Default.CodeGraphResponse)!;
            // Both files are untracked, so the wire form put them first as the working tree's changes.
            var direct = await graph.ReadAsync(root, ["src/app.js", "src/helper.js"], TestContext.Current.CancellationToken);
            Assert.Equal(direct.Nodes, cached.Nodes);
            Assert.Equal(direct.Edges, cached.Edges);
            Assert.Equal(direct.FileCount, cached.FileCount);
            Assert.Equal(direct.Truncated, cached.Truncated);
            Assert.Contains(cached.Edges, edge => edge.Kind == "references");
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadAsync_ParsesBatchesSideBySide_AndKeepsEveryOutlineWithItsFile()
    {
        var root = await CreateRepository();
        try
        {
            // More text than one parse batch holds, so several batches run and their results are
            // written back by index: a declaration under the wrong file would show here.
            var padding = string.Concat(Enumerable.Repeat("// padding padding padding padding padding padding padding\n", 1500));
            const int count = 48;
            for (var index = 0; index < count; index++)
                await Write(root, $"src/File{index:00}.cs", $"{padding}class Type{index:00} {{}}\n", Settled);
            Assert.True((long)padding.Length * count > 3 * 1024 * 1024);

            var graph = await new RepositoryCodeGraph().ReadAsync(root, [], TestContext.Current.CancellationToken);
            var files = graph.Nodes.Where(node => node.Kind == "file").ToArray();
            Assert.Equal(Enumerable.Range(0, count).Select(index => $"src/File{index:00}.cs"), files.Select(file => file.Path));
            foreach (var file in files)
            {
                var declaration = Assert.Single(graph.Nodes, node => node.ParentId == file.Id);
                Assert.Equal("Type" + file.Name.Substring(4, 2), declaration.Name);
            }
            Assert.False(graph.Truncated);
        }
        finally { Delete(root); }
    }

    private static bool SameBuffer(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
        MemoryMarshal.TryGetArray(left, out var first) && MemoryMarshal.TryGetArray(right, out var second)
        && ReferenceEquals(first.Array, second.Array);

    private static string[] Declarations(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(node => node.GetProperty("kind").GetString() == "class")
            .Select(node => node.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Files(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(node => node.GetProperty("kind").GetString() == "file")
            .Select(node => node.GetProperty("path").GetString()!).ToArray();
    }

    private static async Task Write(string root, string path, string text, DateTime modifiedUtc)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(full, modifiedUtc);
    }

    private static async Task<string> CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "viberails-graph-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git(root, "init", "--quiet");
        return root;
    }

    // Committed files are not working-tree changes, so they have no priority of their own. Their
    // modified times are set again afterwards: a commit must not make a settled file look fresh.
    private static async Task Commit(string root)
    {
        await Git(root, "add", "--all");
        await Git(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--no-verify", "-m", "fixture");
        var gitDirectory = Path.Combine(root, ".git") + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            if (!file.StartsWith(gitDirectory, StringComparison.OrdinalIgnoreCase)) File.SetLastWriteTimeUtc(file, Settled);
    }

    private static async Task Git(string root, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }

    private static void Delete(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
