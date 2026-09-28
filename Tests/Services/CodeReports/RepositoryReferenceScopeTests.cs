using MintLint;
using VibeRails.DTOs;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public sealed class RepositoryReferenceScopeTests
{
    [Fact]
    public void CSharp_UsesNamespaceAndInterfaceImports_WithoutCrossLanguageOrReverseTestLinks()
    {
        var graph = Build(
            Source("app/Caller.cs", "using Contracts; namespace Product; class Caller { IService service; Helper helper; TestOnly unrelated; }"),
            Source("contracts/IService.cs", "namespace Contracts; interface IService {}"),
            Source("other/IService.cs", "namespace Other; interface IService {}"),
            Source("app/Helper.cs", "namespace Product; class Helper {}"),
            Source("tests/TestOnly.cs", "namespace Tests; class TestOnly {}"),
            Source("client/Helper.ts", "export class Helper {}"),
            Source("client/main.ts", "const TestOnly = 1; const IService = {};"));

        Assert.Equal(new[] { "app/Helper.cs", "contracts/IService.cs" }, Targets(graph, "app/Caller.cs"));
        Assert.Empty(Targets(graph, "client/main.ts"));
        Assert.Contains(FileReferences(graph), edge => edge.Evidence!.Contains("namespace Product") && edge.Evidence.Contains("IService"));
    }

    [Fact]
    public void CSharp_AliasesAndQualifiedNames_DoNotFallBackToUnrelatedBareNames()
    {
        var graph = Build(
            Source("Caller.cs", """
                using LocalContract = Contracts.IService;
                using Outside = External.Service;
                namespace Product;
                class Caller { LocalContract good; Outside external; Contracts.Helper qualified; }
                """),
            Source("IService.cs", "namespace Contracts; interface IService {}"),
            Source("Helper.cs", "namespace Contracts; class Helper {}"),
            Source("unrelated/Service.cs", "namespace Product; class Service {}"),
            Source("unrelated/Outside.cs", "namespace Product; class Outside {}"));

        Assert.Equal(new[] { "Helper.cs", "IService.cs" }, Targets(graph, "Caller.cs"));
        Assert.Contains(FileReferences(graph), edge => edge.Evidence!.Contains("Caller.cs:1 imports Contracts.IService as LocalContract"));
    }

    [Fact]
    public void CSharp_BlockNamespaceUsings_AreLimitedToTheirOwnScope()
    {
        var graph = Build(
            Source("Caller.cs", """
                namespace First { using Contracts; class One { IService service; } }
                namespace Second { class Two { Missing missing; } }
                """),
            Source("IService.cs", "namespace Contracts; interface IService {}"),
            Source("Missing.cs", "namespace Contracts; class Missing {}"),
            Source("Unused.cs", "namespace First; class Missing {}"));

        Assert.Equal(new[] { "IService.cs" }, Targets(graph, "Caller.cs"));
    }

    [Fact]
    public void CSharp_AmbiguousImportedTypes_AndDeclarationNames_DoNotInventReferences()
    {
        var graph = Build(
            Source("Caller.cs", "using First; using Second; namespace Product; class Caller { IService service; }"),
            Source("First.cs", "namespace First; interface IService {} class Caller {}"),
            Source("Second.cs", "namespace Second; interface IService {}"));
        Assert.Empty(Targets(graph, "Caller.cs"));
    }

    [Fact]
    public void Php_UsesAliasesGroupedImportsAndNamespacePeers_WithImportLineEvidence()
    {
        var graph = Build(
            Source("Caller.php", """
                <?php
                namespace App;
                use Contracts\Handler as LocalHandler;
                use Contracts\{Gateway as LocalGateway, Service};
                use External\Response;
                class Caller { public LocalHandler $handler; public LocalGateway $gateway; public Service $service; public Peer $peer; public Response $external; }
                """),
            Source("Handler.php", "<?php namespace Contracts; interface Handler {}"),
            Source("Gateway.php", "<?php namespace Contracts; interface Gateway {}"),
            Source("Service.php", "<?php namespace Contracts; class Service {}"),
            Source("Peer.php", "<?php namespace App; class Peer {}"),
            Source("wrong/Handler.php", "<?php namespace Unrelated; class Handler {}"),
            Source("wrong/Response.php", "<?php namespace App; class Response {}"));

        Assert.Equal(new[] { "Gateway.php", "Handler.php", "Peer.php", "Service.php" }, Targets(graph, "Caller.php"));
        Assert.Contains(FileReferences(graph), edge => edge.Evidence!.Contains("Caller.php:3 imports Contracts\\Handler as LocalHandler"));
    }

    [Fact]
    public void Php_QualifiedNamesAndCaseInsensitiveTypes_StayWithinTheirNamespaces()
    {
        var graph = Build(
            Source("Caller.php", "<?php namespace App; class Caller { public \\Contracts\\Handler $good; public peer $same; public namespace\\Peer $relative; public Handler $unknown; }"),
            Source("Handler.php", "<?php namespace Contracts; interface Handler {}"),
            Source("Peer.php", "<?php namespace App; class Peer {}"),
            Source("Unrelated.php", "<?php namespace Other; class Handler {}"));
        Assert.Equal(new[] { "Handler.php", "Peer.php" }, Targets(graph, "Caller.php"));
    }

    [Fact]
    public void TypeScript_TypeAndValueNamespaces_LinkOnlyExplicitImports()
    {
        var graph = Build(
            Source("app/main.ts", """
                import type { Response as LocalResponse } from '../types/response';
                import { build as make } from '../runtime/build';
                import type { Widget } from 'external';
                const Response = 1;
                const value: LocalResponse = make();
                """),
            Source("types/response.ts", "export interface Response { status: number }") ,
            Source("runtime/build.ts", "export function build() { return { status: 200 }; }") ,
            Source("unrelated/Widget.ts", "export class Widget {}"),
            Source("server/Response.cs", "class Response {}"));
        Assert.Equal(new[] { "runtime/build.ts", "types/response.ts" }, Targets(graph, "app/main.ts"));
    }

    [Fact]
    public void Python_ExternalAliasesDoNotLinkLocalClasses_AndLocalImportHasItsOwnLine()
    {
        var graph = Build(
            Source("pkg/app.py", """
                from werkzeug.wrappers import Response
                from .wrappers import Response as LocalResponse
                def run():
                    return LocalResponse()
                """),
            Source("pkg/external.py", "from werkzeug.wrappers import Response as Remote\nresult = Remote()"),
            Source("pkg/wrappers.py", "class Response:\n    pass"),
            Source("pkg/__init__.py", ""));
        Assert.Equal(new[] { "pkg/wrappers.py" }, Targets(graph, "pkg/app.py"));
        Assert.Empty(Targets(graph, "pkg/external.py"));
        var edge = Assert.Single(FileReferences(graph), edge => graph.Nodes.Single(node => node.Id == edge.Source).Path == "pkg/app.py");
        Assert.Contains("pkg/app.py:2", edge.Evidence!);
        Assert.DoesNotContain("werkzeug", edge.Evidence!);
    }

    [Fact]
    public void Rust_EnumVariantsDoNotLinkSameNamedTypes_InAnotherCrate()
    {
        var graph = Build(
            Source("crates/core/main.rs", "mod worker; enum Mode { Index } fn main() { let mode = Mode::Index; worker::run(); }"),
            Source("crates/core/worker.rs", "pub fn run() {}"),
            Source("crates/index/src/index.rs", "pub struct Index {}"));
        Assert.Equal(new[] { "crates/core/worker.rs" }, Targets(graph, "crates/core/main.rs"));
    }

    private static CodeGraphResponse Build(params (string Path, SourceOutline? Outline)[] files) =>
        RepositoryCodeGraph.Build("scope", files, false, TestContext.Current.CancellationToken);

    private static (string Path, SourceOutline? Outline) Source(string path, string text) => (path, SourceOutline.Read(path, text));

    private static IEnumerable<CodeGraphEdge> FileReferences(CodeGraphResponse graph) => graph.Edges
        .Where(edge => edge.Kind == "references" && graph.Nodes.Single(node => node.Id == edge.Source).Kind == "file");

    private static string[] Targets(CodeGraphResponse graph, string source)
    {
        var id = graph.Nodes.Single(node => node.Kind == "file" && node.Path == source).Id;
        return FileReferences(graph).Where(edge => edge.Source == id)
            .Select(edge => graph.Nodes.Single(node => node.Id == edge.Target).Path!).Order(StringComparer.Ordinal).ToArray();
    }
}
