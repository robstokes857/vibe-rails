using System.Diagnostics;
using System.Text;
using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.MintLintTests;

public sealed class TypeScriptSourceOutlineTests
{
    [Fact]
    public void MalformedAlias_WithUnmatchedGenericDelimiters_IsReadInLinearTime()
    {
        // 120,009 bytes: inside the graph route's 128 KiB per-file limit. Rescanning the rest
        // of the file from every unmatched `<` previously took about 19 seconds of CPU.
        var source = "type X = " + string.Concat(Enumerable.Repeat("< ", 60_000));
        Assert.Equal(120_009, Encoding.UTF8.GetByteCount(source));

        _ = SourceOutline.Read("warm.ts", "type W = Array<number>;");
        var stopwatch = Stopwatch.StartNew();
        var outline = SourceOutline.Read("broken.ts", source);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Reading took {stopwatch.Elapsed}.");
        Assert.Equal(new[] { new SourceOutlineSymbol("X", "type", 1) }, outline.Declarations);
    }

    [Fact]
    public void ManyDeclarations_WithUnclosedTypeParameters_AreReadInLinearTime()
    {
        // Each `type Ai<` used to scan to the end of the file before being skipped.
        var source = string.Concat(Enumerable.Range(0, 8_000).Select(index => $"type A{index}<\n"));
        Assert.True(Encoding.UTF8.GetByteCount(source) < 128 * 1024);

        var stopwatch = Stopwatch.StartNew();
        var outline = SourceOutline.Read("unclosed.ts", source);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Reading took {stopwatch.Elapsed}.");
        Assert.Empty(outline.Declarations);
    }

    [Fact]
    public void TypeArguments_InsideBlocksAndAfterUnmatchedDelimiters_StillPair()
    {
        var outline = SourceOutline.Read("scoped.ts", """
            declare namespace Api {
                interface Page<T> extends Base<T> { items: T[] }
                type Loader<T> = (id: string) => Promise<T>
            }
            type Broken = Foo<
            type After<T> = Map<string, Array<T>>;
            interface Last { value: (a: number, b: number) => boolean }
            """);

        Assert.Equal(new[]
        {
            new SourceOutlineSymbol("Page", "interface", 2),
            new SourceOutlineSymbol("Loader", "type", 3),
            new SourceOutlineSymbol("Broken", "type", 5),
            new SourceOutlineSymbol("After", "type", 6),
            new SourceOutlineSymbol("Last", "interface", 7)
        }, outline.Declarations);
    }

    [Theory]
    [InlineData(".ts")]
    [InlineData(".tsx")]
    [InlineData(".mts")]
    [InlineData(".cts")]
    public void NamedTypes_HaveTheirOwnKindsAndOriginalLines(string extension)
    {
        var outline = SourceOutline.Read("environment" + extension, """
            export interface Options<T = { enabled: boolean }> extends Base<T>, Record<string, T> {
                value: T;
            }
            export type Environment = DevelopmentEnvironment | BuildEnvironment;
            type Nested<T extends Record<string, Array<number>>>= T;
            export class Runtime {}
            export function start() {}
            """);

        Assert.Equal(new[]
        {
            new SourceOutlineSymbol("Options", "interface", 1),
            new SourceOutlineSymbol("Environment", "type", 4),
            new SourceOutlineSymbol("Nested", "type", 5),
            new SourceOutlineSymbol("Runtime", "class", 6),
            new SourceOutlineSymbol("start", "function", 7)
        }, outline.Declarations);
    }

    [Fact]
    public void NamedTypes_IgnoreStringsCommentsAndImportAliases()
    {
        var outline = SourceOutline.Read("types.ts", """
            // interface CommentGhost { value: string }
            /* type BlockGhost = number; */
            const text = "interface StringGhost { value: string }";
            const template = `type TemplateGhost = number;`;
            import type Imported = require('./imported');
            import type { Remote } from './remote';
            export type { Remote } from './remote';
            export interface Actual { value: string }
            type ActualAlias = Actual;
            """);

        Assert.Equal(new[]
        {
            new SourceOutlineSymbol("Actual", "interface", 8),
            new SourceOutlineSymbol("ActualAlias", "type", 9)
        }, outline.Declarations);
        Assert.DoesNotContain(outline.References, item => item.Name.EndsWith("Ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionTypeSignatures_AreNotMappedAsExecutableFunctions()
    {
        var outline = SourceOutline.Read("types.ts", """
            interface Factory {
                create: (one: string, two: string, three: string, four: string, five: string) => string;
            }
            type Handler = (one: string, two: string, three: string, four: string, five: string) => string
            export type State =
                | { ready: true }
                | { ready: false; reason: string }
            export function execute() {}
            """);

        Assert.Equal(new[]
        {
            new SourceOutlineSymbol("Factory", "interface", 1),
            new SourceOutlineSymbol("Handler", "type", 4),
            new SourceOutlineSymbol("State", "type", 5),
            new SourceOutlineSymbol("execute", "function", 8)
        }, outline.Declarations);
    }

    [Theory]
    [InlineData("async function run() {}")]
    [InlineData("async /* asynchronous work */ function run() {}")]
    [InlineData("async function* run() {}")]
    public void SemicolonlessAlias_StopsBeforeAsyncFunctionDeclaration(string function)
    {
        var outline = SourceOutline.Read("types.ts", "type X = string\n" + function);

        Assert.Equal(new[]
        {
            new SourceOutlineSymbol("X", "type", 1),
            new SourceOutlineSymbol("run", "function", 2)
        }, outline.Declarations);
    }

    [Fact]
    public void MultilineAlias_ContainingAsyncTypeName_StillExcludesFunctionTypeSignatures()
    {
        var outline = SourceOutline.Read("types.ts", """
            type async = { ready: boolean };
            type Handler = string |
                async |
                ((one: string, two: string, three: string, four: string, five: string) => string)
            async function run() {}
            """);

        Assert.Equal(new[]
        {
            new SourceOutlineSymbol("async", "type", 1),
            new SourceOutlineSymbol("Handler", "type", 2),
            new SourceOutlineSymbol("run", "function", 5)
        }, outline.Declarations);
    }

    [Fact]
    public void TypeScriptNamedTypes_DoNotBecomeJavaScriptDeclarationsOrAnalyzerClasses()
    {
        const string types = "interface Options { value: string }\ntype Environment = Options;";
        Assert.Empty(SourceOutline.Read("types.js", types).Declarations);
        var metrics = Assert.Single(MintLintAnalyzer.AnalyzeSources([new SourceInput("types.ts", types)]));
        Assert.Empty(metrics.Classes);
    }

    [Fact]
    public void SmallGraph_ContainsInterfaceAndAliasWithoutHittingGlobalLimits()
    {
        const string path = "src/environment.ts";
        var outline = SourceOutline.Read(path, """
            export interface EnvironmentOptions { name: string }
            export type Environment = EnvironmentOptions & { mode: 'build' | 'dev' };
            """);
        var graph = RepositoryCodeGraph.Build("fixture", [(path, outline)], false, TestContext.Current.CancellationToken);

        Assert.False(graph.Truncated);
        var file = Assert.Single(graph.Nodes, node => node.Kind == "file");
        Assert.Contains(graph.Nodes, node => node.Kind == "interface" && node.Name == "EnvironmentOptions"
            && node.Path == path + ":1" && node.ParentId == file.Id);
        Assert.Contains(graph.Nodes, node => node.Kind == "type" && node.Name == "Environment"
            && node.Path == path + ":2" && node.ParentId == file.Id);
        Assert.Equal(4, graph.Nodes.Count);
    }
}
