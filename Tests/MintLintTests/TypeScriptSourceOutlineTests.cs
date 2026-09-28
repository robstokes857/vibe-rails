using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.MintLintTests;

public sealed class TypeScriptSourceOutlineTests
{
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
