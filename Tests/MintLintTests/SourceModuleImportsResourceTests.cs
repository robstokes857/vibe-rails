using System.Text;
using MintLint;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.MintLintTests;

public sealed class SourceModuleImportsResourceTests
{
    [Fact]
    public void PythonImport_WithThousandsOfSegments_IsBuiltInLinearSpace()
    {
        // 32,008 bytes: inside the graph route's 128 KiB per-file limit, this previously
        // allocated about 980 MiB by re-copying the dotted prefix for every segment.
        var source = "import " + string.Join('.', Enumerable.Repeat("a", 16_001));
        Assert.Equal(32_008, Encoding.UTF8.GetByteCount(source));

        Warm();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outline = SourceOutline.Read("deep.py", source);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 8L * 1024 * 1024, $"A long dotted import allocated {allocated:N0} bytes.");
        Assert.False(outline.ImportEvidenceTruncated);
        var import = Assert.Single(outline.ImportEvidence);
        Assert.Equal("python-import", import.Kind);
        Assert.Equal(32_001, import.Path.Length);
        GC.KeepAlive(outline);
    }

    [Fact]
    public void RustUse_WithThousandsOfSegments_IsBuiltInLinearSpace()
    {
        // 48,006 bytes, previously about 2.2 GiB allocated for the same reason.
        var source = "use " + string.Join("::", Enumerable.Repeat("a", 16_001)) + ";";
        Assert.Equal(48_006, Encoding.UTF8.GetByteCount(source));

        Warm();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outline = SourceOutline.Read("deep.rs", source);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 8L * 1024 * 1024, $"A long use path allocated {allocated:N0} bytes.");
        Assert.False(outline.ImportEvidenceTruncated);
        var import = Assert.Single(outline.ImportEvidence);
        Assert.Equal("rust-use", import.Kind);
        Assert.Equal(48_001, import.Path.Length);
        GC.KeepAlive(outline);
    }

    [Fact]
    public void RustUseGroup_RepeatingALongPrefixPerLeaf_StopsAtTheBudgetAndReportsIt()
    {
        // Every leaf of the group receives its own copy of the 24,000-character prefix, so a
        // 72 KiB source would otherwise expand into roughly 190 million characters of paths.
        var source = "use " + string.Join("::", Enumerable.Repeat("a", 8_000)) + "::{"
            + string.Join(", ", Enumerable.Range(0, 8_000).Select(index => $"b{index}")) + "};";
        Assert.True(Encoding.UTF8.GetByteCount(source) < 128 * 1024);

        Warm();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outline = SourceOutline.Read("group.rs", source);
        var graph = RepositoryCodeGraph.Build("resource", [("group.rs", outline)], false,
            TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 8L * 1024 * 1024, $"A prefix-repeating use group allocated {allocated:N0} bytes.");
        Assert.True(outline.ImportEvidenceTruncated);
        Assert.NotEmpty(outline.ImportEvidence);
        Assert.All(outline.ImportEvidence, import => Assert.StartsWith("a::a::", import.Path));
        // The 256 Ki-character budget documented in the code-report README.
        Assert.True(outline.ImportEvidence.Sum(import => import.Path.Length) <= 256 * 1024);
        Assert.True(graph.Truncated);
        Assert.Contains(graph.Diagnostics!.Omissions, omission => omission.Code == "import-evidence-limit" && omission.Count == 1);
        GC.KeepAlive(outline);
        GC.KeepAlive(graph);
    }

    [Fact]
    public void PythonFromImport_WithThousandsOfNames_SharesTheModuleAndIsNotTruncated()
    {
        var source = "from package.module.deep import (" + string.Join(", ", Enumerable.Range(0, 12_000).Select(index => $"n{index}")) + ")";
        Assert.True(Encoding.UTF8.GetByteCount(source) < 128 * 1024);

        var outline = SourceOutline.Read("names.py", source);

        Assert.False(outline.ImportEvidenceTruncated);
        Assert.Equal(12_000, outline.ImportEvidence.Count);
        Assert.All(outline.ImportEvidence, import => Assert.Equal("package.module.deep", import.Path));
    }

    [Fact]
    public void RustUseGroup_KeepsSelfAndAliasesAndLeadingQualifier()
    {
        var outline = SourceOutline.Read("lib.rs", """
            use ::std::{self, io::{self as sio, Read}};
            use crate::a::b::{self};
            """);

        Assert.Equal(new[]
        {
            new SourceOutlineImport("::std", "rust-use", 1, Scope: ""),
            new SourceOutlineImport("::std::io", "rust-use", 1, Alias: "sio", Scope: ""),
            new SourceOutlineImport("::std::io::Read", "rust-use", 1, Scope: ""),
            new SourceOutlineImport("crate::a::b", "rust-use", 2, Scope: "")
        }, outline.ImportEvidence);
    }

    // Warm static parser initialization outside the measured section. Current-thread allocation
    // is deterministic for this synchronous path and independent of parallel tests.
    private static void Warm()
    {
        _ = SourceOutline.Read("warm.py", "import os.path as paths\nfrom . import json");
        _ = SourceOutline.Read("warm.rs", "use crate::{flags::{self, Mode as Selected}, logger::*};");
        _ = RepositoryCodeGraph.Build("warmup", [("warm.rs", SourceOutline.Read("warm.rs", "mod flags;"))], false,
            TestContext.Current.CancellationToken);
    }
}
