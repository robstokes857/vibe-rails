using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public class RepositoryModuleResolverTests
{
    [Fact]
    public void PythonImports_KeepRelativeDotsAliasesAndMultilineMembers()
    {
        var outline = SourceOutline.Read("src/flask/app.py", """
            from . import json as codec
            from .signals import (
                started as began,
                finished,
            )
            from ...shared import helper
            import os.path as paths, flask.globals as globals
            # from . import imaginary
            text = "from . import imaginary"
            """);
        Assert.Equal(6, outline.ImportEvidence.Count);
        Assert.Contains(outline.ImportEvidence, import => import.Path == "." && import.ImportedName == "json"
            && import.Alias == "codec" && import.Line == 1);
        Assert.Contains(outline.ImportEvidence, import => import.Path == ".signals" && import.ImportedName == "started"
            && import.Alias == "began" && import.Line == 2);
        Assert.Contains(outline.ImportEvidence, import => import.Path == "...shared");
        Assert.Contains(outline.ImportEvidence, import => import.Path == "os.path" && import.Alias == "paths");
    }

    [Fact]
    public void PythonImports_ResolveFlaskPackagesAndFunctionOnlyModules()
    {
        var files = Sources(
            ("src/flask/__init__.py", "from .globals import current_app\nfrom . import json as codec"),
            ("src/flask/app.py", "from .signals import started as begin\nfrom flask.helpers import work"),
            ("src/flask/globals.py", "current_app = None"),
            ("src/flask/signals.py", "def started():\n    pass"),
            ("src/flask/helpers.py", "def work():\n    pass"),
            ("src/flask/json/__init__.py", "def loads():\n    pass"),
            ("tests/test_app.py", "import flask.app as app"));
        var resolver = new RepositoryModuleResolver(files);
        Assert.Equal(["src/flask/globals.py", "src/flask/json/__init__.py"], Targets(resolver, files, "src/flask/__init__.py"));
        Assert.Equal(["src/flask/signals.py", "src/flask/helpers.py"], Targets(resolver, files, "src/flask/app.py"));
        Assert.Equal(["src/flask/app.py"], Targets(resolver, files, "tests/test_app.py"));
    }

    [Fact]
    public void PythonImports_DoNotGuessBasenamesExternalModulesOrEscapePackages()
    {
        var files = Sources(
            ("src/one/__init__.py", ""),
            ("src/one/app.py", "from .signals import started\nfrom werkzeug import Response\nimport json\nfrom ..two import signals\nfrom ... import outside"),
            ("src/one/signals.py", ""),
            ("src/one/json.py", ""),
            ("src/one/wrappers.py", "class Response:\n    pass"),
            ("src/two/__init__.py", ""),
            ("src/two/signals.py", ""),
            ("outside.py", ""));
        Assert.Equal(["src/one/signals.py"], Targets(new(files), files, "src/one/app.py"));
    }

    [Fact]
    public void PythonImports_UseNearestPackageRootAndRejectAmbiguousAbsolutePackages()
    {
        var files = Sources(
            ("first/src/pkg/__init__.py", ""),
            ("first/src/pkg/app.py", "from pkg.signals import started\nfrom .signals import started"),
            ("first/src/pkg/signals.py", ""),
            ("second/src/pkg/__init__.py", ""),
            ("second/src/pkg/signals.py", ""),
            ("tests/test_pkg.py", "import pkg.signals"));
        var resolver = new RepositoryModuleResolver(files);
        Assert.Equal(["first/src/pkg/signals.py", "first/src/pkg/signals.py"], Targets(resolver, files, "first/src/pkg/app.py"));
        Assert.Empty(Targets(resolver, files, "tests/test_pkg.py"));
    }

    [Fact]
    public void PythonImports_ResolveParentPackageAndNamespaceSrcLayout()
    {
        var files = Sources(
            ("src/pkg/sub/app.py", "from ..shared import work\nfrom . import util"),
            ("src/pkg/shared.py", ""),
            ("src/pkg/sub/util.py", ""));
        Assert.Equal(["src/pkg/shared.py", "src/pkg/sub/util.py"], Targets(new(files), files, "src/pkg/sub/app.py"));
    }

    [Fact]
    public void PythonImports_DoNotTreatAnExistingPackageNamedSrcAsAnImportRoot()
    {
        var files = Sources(
            ("pkg/__init__.py", ""), ("pkg/src/__init__.py", ""),
            ("pkg/src/app.py", "from ..shared import work\nfrom . import util"),
            ("pkg/shared.py", ""), ("pkg/src/util.py", ""));
        Assert.Equal(["pkg/shared.py", "pkg/src/util.py"], Targets(new(files), files, "pkg/src/app.py"));
    }

    [Fact]
    public void RustImports_KeepGroupedAliasesAndInlineScopesAndIgnoreMacros()
    {
        var outline = SourceOutline.Read("src/lib.rs", """
            mod flags;
            use crate::{flags::{self, Mode as Selected}, logger::*};
            mod inline {
                use super::flags::Mode;
                pub mod child;
            }
            // mod ignored;
            macro_rules! generated { () => { mod ignored; use crate::ignored; } }
            generated! { mod ignored; }
            #[path = "custom.rs"] mod custom;
            """);
        Assert.Equal(7, outline.ImportEvidence.Count);
        Assert.Contains(outline.ImportEvidence, import => import.Kind == "rust-use" && import.Path == "crate::flags");
        Assert.Contains(outline.ImportEvidence, import => import.Path == "crate::flags::Mode" && import.Alias == "Selected");
        Assert.Contains(outline.ImportEvidence, import => import.Path == "super::flags::Mode" && import.Scope == "inline");
        Assert.Contains(outline.ImportEvidence, import => import.Kind == "rust-mod" && import.Path == "child" && import.Scope == "inline");
        Assert.DoesNotContain(outline.ImportEvidence, import => import.Path.Contains("ignored") || import.Path == "custom");
    }

    [Fact]
    public void RustImports_ResolveDeclaredModulesScopedPathsAndGroupedItems()
    {
        var files = Sources(
            ("crates/core/main.rs", "mod flags; mod logger; mod search; use crate::{flags::Mode, logger::log};"),
            ("crates/core/flags/mod.rs", "pub mod defs; use self::defs::Flag; use super::logger::log;"),
            ("crates/core/flags/defs.rs", "pub struct Flag {}"),
            ("crates/core/logger.rs", "pub fn log() {}"),
            ("crates/core/search.rs", "mod engine; use crate::flags::Mode; use self::engine::run;"),
            ("crates/core/search/engine.rs", "use super::super::logger::log;"));
        var resolver = new RepositoryModuleResolver(files, ["Cargo.toml", "crates/core/Cargo.toml"]);
        Assert.Equal(["crates/core/flags/mod.rs", "crates/core/logger.rs", "crates/core/search.rs",
            "crates/core/flags/mod.rs", "crates/core/logger.rs"], Targets(resolver, files, "crates/core/main.rs"));
        Assert.Equal(["crates/core/flags/defs.rs", "crates/core/flags/defs.rs", "crates/core/logger.rs"],
            Targets(resolver, files, "crates/core/flags/mod.rs"));
        Assert.Equal(["crates/core/search/engine.rs", "crates/core/flags/mod.rs", "crates/core/search/engine.rs"],
            Targets(resolver, files, "crates/core/search.rs"));
        Assert.Equal(["crates/core/logger.rs"], Targets(resolver, files, "crates/core/search/engine.rs"));
    }

    [Fact]
    public void RustImports_ResolveInlineModuleChildrenAndSuper()
    {
        var files = Sources(
            ("src/lib.rs", "mod helpers; mod inner { pub mod child; use super::helpers::work; }"),
            ("src/helpers.rs", ""),
            ("src/inner/child.rs", "use super::super::helpers::work;"));
        var resolver = new RepositoryModuleResolver(files);
        Assert.Equal(["src/helpers.rs", "src/inner/child.rs", "src/helpers.rs"], Targets(resolver, files, "src/lib.rs"));
        Assert.Equal(["src/helpers.rs"], Targets(resolver, files, "src/inner/child.rs"));
    }

    [Fact]
    public void RustImports_RejectExternalCratesUndeclaredFilesAndWorkspaceCrossing()
    {
        var files = Sources(
            ("crates/app/src/main.rs", "mod local; mod nested; use external::Thing; use crate::missing::Thing; use ::local::Thing;"),
            ("crates/app/src/local.rs", "use super::super::external::Thing;"),
            ("crates/app/src/missing.rs", ""),
            ("crates/app/src/nested/mod.rs", ""),
            ("crates/external/src/lib.rs", "pub struct Thing {}"));
        var resolver = new RepositoryModuleResolver(files,
            ["Cargo.toml", "crates/app/Cargo.toml", "crates/app/src/nested/Cargo.toml", "crates/external/Cargo.toml"]);
        Assert.Equal(["crates/app/src/local.rs"], Targets(resolver, files, "crates/app/src/main.rs"));
        Assert.Empty(Targets(resolver, files, "crates/app/src/local.rs"));
    }

    [Fact]
    public void RustImports_RejectAmbiguousModuleFileLayouts()
    {
        var files = Sources(("src/lib.rs", "mod foo; use crate::foo::Thing;"),
            ("src/foo.rs", ""), ("src/foo/mod.rs", ""));
        Assert.Empty(Targets(new(files), files, "src/lib.rs"));
    }

    [Fact]
    public void Build_UsesModuleEvidenceWithOriginalLinesEvenWithoutClassDeclarations()
    {
        var files = Sources(
            ("src/pkg/__init__.py", "# exports\nfrom . import helpers as functions"),
            ("src/pkg/helpers.py", "def work():\n    pass"),
            ("crates/app/main.rs", "// dependencies\nmod logger;"),
            ("crates/app/logger.rs", "pub fn log() {}"));
        var graph = RepositoryCodeGraph.Build("fixture", files, false, TestContext.Current.CancellationToken);
        var nodes = graph.Nodes.ToDictionary(node => node.Id);
        var references = graph.Edges.Where(edge => edge.Kind == "references" && nodes[edge.Source].Kind == "file").ToArray();
        Assert.Equal(2, references.Length);
        Assert.Contains(references, edge => nodes[edge.Target].Path == "src/pkg/helpers.py"
            && edge.Evidence == "src/pkg/__init__.py:2 imports . (helpers)");
        Assert.Contains(references, edge => nodes[edge.Target].Path == "crates/app/logger.rs"
            && edge.Evidence == "crates/app/main.rs:2 declares module logger");
    }

    private static IReadOnlyList<(string Path, SourceOutline? Outline)> Sources(params (string Path, string Text)[] files)
        => files.Select(file => (file.Path, (SourceOutline?)SourceOutline.Read(file.Path, file.Text))).ToArray();

    private static string[] Targets(RepositoryModuleResolver resolver,
        IReadOnlyList<(string Path, SourceOutline? Outline)> files, string source)
        => resolver.Resolve(source, files.Single(file => file.Path == source).Outline!).Select(target => target.Path).ToArray();
}
