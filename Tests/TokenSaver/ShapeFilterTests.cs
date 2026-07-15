using System.Text;
using TokenSaver.Pipeline;
using TokenSaver.Shape;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Unit tests for <see cref="CommandShapes"/> and <see cref="ShapeFilters"/> — the shape-aware
/// stage. Two bars here. For the classifier: every rejection is a SAFETY rule, so the negative cases
/// (pipes, `git status` long format, rg without -n) matter more than the positives. For the filters:
/// the class doc's invariants (deterministic / idempotent / never grows / fail-open / never throws),
/// NOT the minifier's subsequence conservation — these transforms move and insert text by design.
/// The fixed-point pins are load-bearing: they are why an emitted header/entry must be ineligible.
/// </summary>
public class ShapeFilterTests
{
    // ESC is written as "\u001b" throughout, never \x1b — a following hex
    // digit would silently extend the escape in C# source (OutputMinifierTests' convention).
    private const string Esc = "\u001b";

    [Fact]
    public void Catalog_WiresGitStatusFilter_AndKeepsLossyStagesOptIn()
    {
        var plan = CompressionCatalog.Resolve(null);

        Assert.Contains(CompressionCatalog.GitStatusGroup, plan.EnabledIds);
        Assert.Contains(CommandShape.GitStatus, plan.Shapes);
        Assert.DoesNotContain(CompressionCatalog.DedupeLines, plan.EnabledIds);
        Assert.DoesNotContain(CompressionCatalog.TruncateLong, plan.EnabledIds);
    }

    // ---------------------------------------------------------------------
    // Classify — positives
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("git status --short")]
    [InlineData("git status -s")]
    [InlineData("git status --porcelain")]
    [InlineData("git status --porcelain=v1")]
    [InlineData("git status -s -uall")]
    [InlineData("git status --short --ignored")]
    [InlineData("git status --short -- src/")]
    public void Classify_GitStatus(string command)
    {
        Assert.Equal(CommandShape.GitStatus, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("git log")]
    [InlineData("git log --oneline -20")]
    [InlineData("git log --graph --format=%h")]
    public void Classify_GitLog(string command)
    {
        Assert.Equal(CommandShape.GitLog, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("git diff")]
    [InlineData("git diff --stat")]
    [InlineData("git show HEAD")]
    public void Classify_GitDiff(string command)
    {
        Assert.Equal(CommandShape.GitDiff, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("ls -la")]
    [InlineData("dir")]
    [InlineData("tree -L 2")]
    public void Classify_DirectoryListing(string command)
    {
        Assert.Equal(CommandShape.DirectoryListing, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("rg -n TODO src/")]
    [InlineData("rg --line-number TODO src/")]
    [InlineData("rg -n --type cs TODO")]
    [InlineData("rg -ntcs TODO")] // cluster: -n, then -t swallows "cs" as its value
    [InlineData("rg -n -g *.cs TODO src/")]
    [InlineData("rg -n -e TODO src/")]
    [InlineData("rg --line-number --regexp TODO src/")]
    [InlineData("rg -in --hidden --no-ignore TODO")]
    [InlineData("ripgrep -n TODO src/")]
    [InlineData("grep -rn TODO .")] // grep's -r is --recursive, NOT rg's value-taking --replace
    [InlineData("grep -n TODO file.cs")]
    [InlineData("grep --line-number --ignore-case TODO src/")]
    [InlineData("ag --numbers TODO src/")]
    public void Classify_GrepMatches(string command)
    {
        Assert.Equal(CommandShape.GrepMatches, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("find . -name *.cs")]
    [InlineData("find . -type f")]
    [InlineData("find src -type f -maxdepth 2")]
    [InlineData("find . -name foo -o -name bar")]
    [InlineData("find . -type d -print")]
    public void Classify_PathList(string command)
    {
        Assert.Equal(CommandShape.PathList, CommandShapes.Classify(command));
    }

    // ---------------------------------------------------------------------
    // Classify — the whole-command rule. Everything here has a shape we would
    // otherwise recognize; the metacharacter is the only reason it is None.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("git status -s | head")]
    [InlineData("rg -n TODO src/ | wc -l")]
    [InlineData("git status -s > out.txt")]
    [InlineData("rg -n TODO src/ 2>&1")]
    [InlineData("grep -n TODO < input.txt")]
    [InlineData("git status -s && ls")]
    [InlineData("git status -s || true")]
    [InlineData("git status -s; ls")]
    [InlineData("ls &")]
    [InlineData("git status -s $(pwd)")]
    [InlineData("rg -n $PATTERN src/")]
    [InlineData("git status -s `pwd`")]
    [InlineData("(ls)")]
    [InlineData("rg -n TODO src/\nls")]
    public void Classify_ShellMetacharacters_AreNone(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    // ---------------------------------------------------------------------
    // Classify — negatives
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cat file.txt")]
    [InlineData("echo hi")]
    public void Classify_UnrecognizedOrEmpty_IsNone(string? command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Fact]
    public void Classify_GitStatusLongFormat_IsNone()
    {
        // The single most important positive-looking negative: bare `git status` is prose
        // ("On branch main", "Changes not staged for commit:", indented paths), not XY-path lines.
        Assert.Equal(CommandShape.None, CommandShapes.Classify("git status"));
        Assert.Equal(CommandShape.None, CommandShapes.Classify("git status --long"));
        Assert.Equal(CommandShape.None, CommandShapes.Classify("git status -v"));
    }

    [Theory]
    [InlineData("git status --porcelain=v2")] // a completely different format
    [InlineData("git status -s -z")] // NUL separators destroy the line model
    [InlineData("git status -sb")] // -b adds a "## branch" line
    [InlineData("git status -s --column")]
    public void Classify_GitStatus_ShapeChangingFlags_AreNone(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("rg TODO src/")] // rg only numbers lines for a tty — in a pipe it does not
    [InlineData("grep -r TODO .")]
    [InlineData("ag -n TODO src/")] // ag's -n is --norecurse; line numbers are --numbers
    public void Classify_Grep_WithoutLineNumbers_IsNone(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("rg -e -n file.txt")]          // -n is the required value of -e, not an option
    [InlineData("rg --regexp -n file.txt")]   // same, long-option form
    [InlineData("grep -f -n file.txt")]       // -n names the pattern file consumed by -f
    [InlineData("rg -- TODO -n")]             // after --, -n is a path rather than an option
    public void Classify_Grep_OptionValuesAndEndMarkerCannotInventLineNumbers(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("grep -n -T -l TODO")]       // grep -T takes no value; -l still changes shape
    [InlineData("grep -n --color -l TODO")]  // grep's --color value is optional, not the next flag
    [InlineData("ag --numbers -f -l TODO")]  // ag -f is --follow, not a value-taking option
    public void Classify_Grep_ToolSpecificFlagsCannotHideShapeChangingOptions(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Fact]
    public void Classify_GitStatus_PathspecAfterEndMarkerCannotSelectPorcelainFormat()
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify("git status -- --short"));
    }

    [Theory]
    [InlineData("rg -l TODO")] // --files-with-matches: no line, no content
    [InlineData("rg -nl TODO")] // same, hiding in a cluster
    [InlineData("rg -n --files-with-matches TODO")]
    [InlineData("rg -n -c TODO")] // --count
    [InlineData("rg -n --count TODO")]
    [InlineData("rg -n -o TODO")] // --only-matching
    [InlineData("rg -n -C 3 TODO")] // context lines are path-line-content, and would be reordered
    [InlineData("rg -n --context 3 TODO")]
    [InlineData("rg -n --heading TODO")] // rg's grouped form
    [InlineData("rg -n --vimgrep TODO")] // an extra column field
    [InlineData("rg -n --column TODO")]
    [InlineData("rg -n --json TODO")]
    [InlineData("rg -nU TODO")] // multiline: continuation lines carry no path
    [InlineData("rg -n --no-filename TODO")]
    [InlineData("grep -nh TODO src/")] // -h drops the filename
    [InlineData("rg -n --frobnicate TODO")] // unknown flag: allowlist, not denylist
    public void Classify_Grep_ShapeChangingFlags_AreNone(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Fact]
    public void Classify_Grep_QuotedFlag_IsNone()
    {
        // The shell strips the quotes and rg sees -l, so the classifier must see it too.
        Assert.Equal(CommandShape.None, CommandShapes.Classify("rg -n \"-l\" src/"));
        Assert.Equal(CommandShape.None, CommandShapes.Classify("rg -n '-l' src/"));

        // But a quoted PATTERN is still fine — unquoting is only there to expose flags.
        Assert.Equal(CommandShape.GrepMatches, CommandShapes.Classify("rg -n \"TODO\" src/"));
    }

    [Theory]
    [InlineData("find .")] // no predicate: indistinguishable from Windows' find.exe
    [InlineData("find . -name x -print0")]
    [InlineData("find . -printf %p")]
    [InlineData("find . -ls")]
    [InlineData("find . -type f -delete")]
    public void Classify_Find_ShapeChangingOrAmbiguous_IsNone(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    [Theory]
    [InlineData("sudo ls")]
    [InlineData("command ls")]
    [InlineData("FOO=1 ls")]
    [InlineData("/usr/bin/git status -s")]
    [InlineData("git -C /repo status -s")]
    [InlineData("git --no-pager log")]
    public void Classify_PrefixedOrPathQualified_IsNone(string command)
    {
        // Documented deliberate Nones: the first token must BE the command (see class remarks).
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
    }

    // ---------------------------------------------------------------------
    // G — git status grouping
    // ---------------------------------------------------------------------

    private const string GitStatusFixture =
        " M src/app/main.cs\n" +
        " M src/app/util.cs\n" +
        " M src/app/view.cs\n" +
        " M src/lib/io.cs\n" +
        " M src/lib/net.cs\n" +
        " M tests/app.cs\n" +
        " M tests/lib.cs\n" +
        "?? docs/plan.md\n" +
        "?? docs/notes.md\n" +
        "?? scratch/a.txt\n" +
        "?? scratch/b.txt\n" +
        "?? scratch/c.txt\n";

    [Fact]
    public void G_GroupsByStatus_SortedOrdinally()
    {
        var expected =
            " M:\n" +
            "  src/app/main.cs\n" +
            "  src/app/util.cs\n" +
            "  src/app/view.cs\n" +
            "  src/lib/io.cs\n" +
            "  src/lib/net.cs\n" +
            "  tests/app.cs\n" +
            "  tests/lib.cs\n" +
            "??:\n" +
            "  docs/notes.md\n" +
            "  docs/plan.md\n" +
            "  scratch/a.txt\n" +
            "  scratch/b.txt\n" +
            "  scratch/c.txt\n";

        Assert.Equal(expected, ShapeFilters.Apply(GitStatusFixture, CommandShape.GitStatus));
    }

    [Fact]
    public void G_UnparsedLines_PassThroughAtTheEnd()
    {
        // git writes warnings to stderr and the tool merges the streams, so prose can land above the
        // status lines. Grouping moves it below — the documented cost of grouping.
        var input = "warning: LF will be replaced by CRLF in x.txt\n" + GitStatusFixture;
        var result = ShapeFilters.Apply(input, CommandShape.GitStatus);

        Assert.StartsWith(" M:\n", result);
        Assert.EndsWith("warning: LF will be replaced by CRLF in x.txt\n", result);
    }

    [Fact]
    public void G_GroupedOutput_IsAFixedPoint()
    {
        var once = ShapeFilters.Apply(GitStatusFixture, CommandShape.GitStatus);
        Assert.NotSame(GitStatusFixture, once); // it really did group
        Assert.Same(once, ShapeFilters.Apply(once, CommandShape.GitStatus));
    }

    [Fact]
    public void G_EmittedEntry_WhosePathStartsWithASpace_IsIneligible()
    {
        // Proof G's edge: an entry for a space-led path is "  " + " path", whose status field is
        // "  " — two spaces, which git never prints (it means unmodified). Without that rule the
        // second pass would regroup our own output.
        var input = "?? " + " odd.txt\n?? a.txt\n?? b.txt\n?? c.txt\n?? d.txt\n?? e.txt\n";
        var once = ShapeFilters.Apply(input, CommandShape.GitStatus);

        Assert.Contains("   odd.txt\n", once); // 2 spaces of indent + the path's own leading space
        Assert.Same(once, ShapeFilters.Apply(once, CommandShape.GitStatus));
    }

    [Fact]
    public void G_NonStatusPayload_ReturnsInputUntouched()
    {
        var input = "On branch main\nnothing to commit, working tree clean\n";
        Assert.Same(input, ShapeFilters.Apply(input, CommandShape.GitStatus));
    }

    // ---------------------------------------------------------------------
    // M — grep grouping
    // ---------------------------------------------------------------------

    private const string GrepFixture =
        "src/services/session.cs:12:    var session = new Session();\n" +
        "src/services/session.cs:48:    session.Close();\n" +
        "src/routes/api.cs:7:app.MapGet(\"/session\", Handler);\n";

    [Fact]
    public void M_GroupsByPath_InOriginalOrder()
    {
        // "src/routes" sorts before "src/services" ordinally — the fixture proves we do NOT sort.
        var expected =
            "src/services/session.cs:\n" +
            "  12:     var session = new Session();\n" +
            "  48:     session.Close();\n" +
            "src/routes/api.cs:\n" +
            "  7: app.MapGet(\"/session\", Handler);\n";

        Assert.Equal(expected, ShapeFilters.Apply(GrepFixture, CommandShape.GrepMatches));
    }

    [Fact]
    public void M_GroupedOutput_IsAFixedPoint()
    {
        var once = ShapeFilters.Apply(GrepFixture, CommandShape.GrepMatches);
        Assert.NotSame(GrepFixture, once);
        Assert.Same(once, ShapeFilters.Apply(once, CommandShape.GrepMatches));
    }

    [Fact]
    public void M_ContentContainingAColonDigitsColon_SplitsAtTheFirstField_ThenIsAFixedPoint()
    {
        // Pin M: a naive last-colon (or re-scanning) split would read the content's ":34:" as the
        // line number. And because the emitted entry is indented, the second pass — which WOULD
        // find ":34:" in it — must refuse it on the leading space alone.
        var input =
            "src/services/session.cs:12:  var m = map[x:34: y];\n" +
            "src/services/session.cs:13:  var n = 1;\n" +
            "src/services/session.cs:14:  var o = 2;\n";
        var once = ShapeFilters.Apply(input, CommandShape.GrepMatches);

        Assert.Equal(
            "src/services/session.cs:\n" +
            "  12:   var m = map[x:34: y];\n" +
            "  13:   var n = 1;\n" +
            "  14:   var o = 2;\n",
            once);
        Assert.Same(once, ShapeFilters.Apply(once, CommandShape.GrepMatches));
    }

    [Fact]
    public void M_SingleFileForm_WithNoPathField_IsLeftAlone()
    {
        // grep/rg omit the path when searching one named file. There is nothing to group, and the
        // ":34:" inside the first line's content must NOT invent a file called "12:  var m = map[x".
        var input =
            "12:  var m = map[x:34: y];\n" +
            "13:  var n = 1;\n" +
            "14:  var o = 2;\n";
        Assert.Same(input, ShapeFilters.Apply(input, CommandShape.GrepMatches));
    }

    [Fact]
    public void M_EmptyContent_IsPreserved()
    {
        var input = "a.cs:1:\na.cs:2:\na.cs:3:\na.cs:4:\n";
        var once = ShapeFilters.Apply(input, CommandShape.GrepMatches);

        Assert.Equal("a.cs:\n  1: \n  2: \n  3: \n  4: \n", once);
        Assert.Same(once, ShapeFilters.Apply(once, CommandShape.GrepMatches));
    }

    [Fact]
    public void M_UnparsedLines_PassThroughAtTheEnd()
    {
        var input = GrepFixture + "src/routes/api.cs:9:x\nsrc/routes/api.cs:10:y\nno match here\n";
        var result = ShapeFilters.Apply(input, CommandShape.GrepMatches);

        Assert.EndsWith("no match here\n", result);
    }

    [Fact]
    public void M_NeverGrows_SingleMatch()
    {
        // The invariant's canonical case: grouping one match costs more than it saves.
        var input = "a.cs:1:x\n";
        Assert.Same(input, ShapeFilters.Apply(input, CommandShape.GrepMatches));
    }

    [Fact]
    public void M_UnterminatedFinalLine_StaysUnterminated()
    {
        var input =
            "src/services/session.cs:12:    var session = new Session();\n" +
            "src/services/session.cs:48:    session.Close();\n" +
            "src/services/session.cs:49:    return session";
        var result = ShapeFilters.Apply(input, CommandShape.GrepMatches);

        Assert.EndsWith("  49:     return session", result);
        Assert.DoesNotContain("\n\n", result);
    }

    // ---------------------------------------------------------------------
    // P — path list grouping
    // ---------------------------------------------------------------------

    private const string PathListFixture =
        "./src/app/main.cs\n" +
        "./src/app/util.cs\n" +
        "./src/app/view.cs\n" +
        "./src/lib/io.cs\n";

    [Fact]
    public void P_GroupsByDirectory()
    {
        var expected =
            "./src/app:\n" +
            "  main.cs\n" +
            "  util.cs\n" +
            "  view.cs\n" +
            "./src/lib:\n" +
            "  io.cs\n";

        Assert.Equal(expected, ShapeFilters.Apply(PathListFixture, CommandShape.PathList));
    }

    [Fact]
    public void P_GroupedOutput_IsAFixedPoint()
    {
        var once = ShapeFilters.Apply(PathListFixture, CommandShape.PathList);
        Assert.NotSame(PathListFixture, once);
        Assert.Same(once, ShapeFilters.Apply(once, CommandShape.PathList));
    }

    [Fact]
    public void P_BackslashPaths_AreNotRewritten()
    {
        var input =
            "C:\\src\\app\\main.cs\n" +
            "C:\\src\\app\\util.cs\n" +
            "C:\\src\\app\\view.cs\n" +
            "C:\\src\\lib\\io.cs\n";
        var result = ShapeFilters.Apply(input, CommandShape.PathList);

        Assert.Equal(
            "C:\\src\\app:\n  main.cs\n  util.cs\n  view.cs\nC:\\src\\lib:\n  io.cs\n",
            result);
        Assert.DoesNotContain('/', result); // the prototype normalized separators; we do not
    }

    [Fact]
    public void P_NeverGrows_SinglePath()
    {
        var input = "src/a.cs\n";
        Assert.Same(input, ShapeFilters.Apply(input, CommandShape.PathList));
    }

    // ---------------------------------------------------------------------
    // The v1 no-ops
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(CommandShape.DirectoryListing)]
    [InlineData(CommandShape.GitLog)]
    [InlineData(CommandShape.GitDiff)]
    [InlineData(CommandShape.None)]
    public void NoOpShapes_ReturnTheSameInstance(CommandShape shape)
    {
        // Deliberate no-ops in v1 — pinned so nobody "improves" one without giving it a proof first.
        Assert.Same(GitStatusFixture, ShapeFilters.Apply(GitStatusFixture, shape));
        Assert.Same(GrepFixture, ShapeFilters.Apply(GrepFixture, shape));
        Assert.Same(PathListFixture, ShapeFilters.Apply(PathListFixture, shape));
    }

    [Fact]
    public void UnknownShapeValue_ReturnsTheSameInstance()
    {
        Assert.Same(GrepFixture, ShapeFilters.Apply(GrepFixture, (CommandShape)9999));
    }

    // ---------------------------------------------------------------------
    // Fail-open — ESC/BEL/CR
    // ---------------------------------------------------------------------

    public static TheoryData<string> ControlCharacterPayloads() =>
    [
        // A payload that would otherwise group cleanly, with one control char somewhere in it.
        "src/a.cs:1:" + Esc + "[31mred" + Esc + "[0m\nsrc/a.cs:2:y\nsrc/a.cs:3:z\nsrc/a.cs:4:w\n",
        "src/a.cs:1:bell\a\nsrc/a.cs:2:y\nsrc/a.cs:3:z\nsrc/a.cs:4:w\n",
        "src/a.cs:1:progress\rdone\nsrc/a.cs:2:y\nsrc/a.cs:3:z\nsrc/a.cs:4:w\n",
        "src/a.cs:1:x\r\nsrc/a.cs:2:y\r\nsrc/a.cs:3:z\r\nsrc/a.cs:4:w\r\n",
        " M src/a.cs\r\n M src/b.cs\r\n M src/c.cs\r\n M src/d.cs\r\n M src/e.cs\r\n",
        "./src/a.cs\r\n./src/b.cs\r\n./src/c.cs\r\n./src/d.cs\r\n",
    ];

    [Theory]
    [MemberData(nameof(ControlCharacterPayloads))]
    public void Abort_ControlCharacters_LeaveWholeStringUntouched(string input)
    {
        // Same rule as OutputCondenser: never edit lines near escape sequences or CR frames.
        foreach (var shape in AllShapeValues)
            Assert.Same(input, ShapeFilters.Apply(input, shape));
    }

    // ---------------------------------------------------------------------
    // API contract
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_EmptyInput_ReturnsInput()
    {
        Assert.Same(string.Empty, ShapeFilters.Apply(string.Empty, CommandShape.GrepMatches));
    }

    // ---------------------------------------------------------------------
    // Property tests — every shape × adversarial corpus. Hand-rolled,
    // mirroring OutputCondenserTests: idempotency, determinism, never-grows,
    // and never-throws. NOT subsequence conservation — these transforms move
    // and insert text by design.
    // ---------------------------------------------------------------------

    private static readonly CommandShape[] AllShapeValues = Enum.GetValues<CommandShape>();

    internal static IEnumerable<string> AdversarialInputs()
    {
        // Control-character inputs (all must hit the fail-open path).
        yield return "a\r";
        yield return "\r\n";
        yield return Esc + "[0;1;2m";
        yield return "bell\a";
        yield return "src/a.cs:1:x\r\nsrc/a.cs:2:y\r\n";

        // Plain edges.
        yield return "";
        yield return "a";
        yield return "\n";
        yield return "\n\n\n";
        yield return ":";
        yield return "::::";
        yield return ":1:";
        yield return "héllo\nwörld";
        yield return "\U0001D11E\n\U0001D11E:1:\U0001D11E";

        // git status shapes, including the ones that must stay ineligible.
        yield return GitStatusFixture;
        yield return GitStatusFixture.TrimEnd('\n');
        yield return " M a\n";
        yield return "?? \n"; // status but empty path
        yield return "?? a\n?? b\n?? c\n?? d\n?? e\n?? f\n";
        yield return "?? " + " leading-space.txt\n?? a\n?? b\n?? c\n?? d\n?? e\n";
        yield return "  two-space-status\n  another\n  third\n";
        yield return "## main...origin/main\n M a.cs\n M b.cs\n M c.cs\n M d.cs\n M e.cs\n";
        yield return "R  old.cs -> new.cs\nR  a.cs -> b.cs\nR  c.cs -> d.cs\nR  e.cs -> f.cs\nR  g.cs -> h.cs\n";
        yield return "On branch main\nnothing to commit, working tree clean\n";

        // grep shapes, including every parse trap the proof rests on.
        yield return GrepFixture;
        yield return GrepFixture.TrimEnd('\n');
        yield return "a.cs:1:x\n";
        yield return "a.cs:1:\na.cs:2:\na.cs:3:\na.cs:4:\n";
        yield return "src/a.cs:12:x:34:y\nsrc/a.cs:13:z\nsrc/a.cs:14:w\nsrc/a.cs:15:v\n";
        yield return "12:  var m = map[x:34: y];\n13:  n\n14:  o\n"; // no path field
        yield return "  4: already indented\n  5: like our own output\n";
        yield return "a:12:b:7:c\na:13:d\na:14:e\na:15:f\n";
        yield return "C:\\src\\a.cs:12:x\nC:\\src\\a.cs:13:y\nC:\\src\\a.cs:14:z\nC:\\src\\a.cs:15:w\n";
        yield return "a.cs:0012:padded\na.cs:0013:padded\na.cs:0014:padded\na.cs:0015:padded\n";
        yield return "no colon here\nnor here\n";
        yield return "path with space.cs:1:x\npath with space.cs:2:y\npath with space.cs:3:z\n";

        // path shapes.
        yield return PathListFixture;
        yield return PathListFixture.TrimEnd('\n');
        yield return "/usr/bin/x\n/usr/bin/y\n/usr/bin/z\n/usr/lib/w\n";
        yield return "src/\nsrc/a.cs\nsrc/b.cs\nsrc/c.cs\n";
        yield return ".\n./a\n./b\n./c\n";
        yield return "bare.cs\nother.cs\nthird.cs\nfourth.cs\n";
        yield return "./src/app:\n  main.cs\n  util.cs\n"; // our own output, fed back in

        // A payload big enough that grouping definitely fires.
        var many = new StringBuilder();
        for (var i = 0; i < 200; i++)
            many.Append("src/generated/module").Append(i % 7).Append(".cs:").Append(i).Append(":  code line ").Append(i).Append('\n');
        yield return many.ToString();
    }

    public static TheoryData<CommandShape> AllShapes()
    {
        var data = new TheoryData<CommandShape>();
        foreach (var shape in AllShapeValues)
            data.Add(shape);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Properties_IdempotentDeterministicNeverGrows(CommandShape shape)
    {
        foreach (var input in AdversarialInputs())
        {
            // (0) never throws — any escape from Apply fails the test here.
            var once = ShapeFilters.Apply(input, shape);

            // (1) idempotency: Apply(Apply(x, s), s) == Apply(x, s)
            var twice = ShapeFilters.Apply(once, shape);
            Assert.True(once == twice,
                $"Not idempotent for shape={shape} input=\"{Show(input)}\": " +
                $"once=\"{Show(once)}\" twice=\"{Show(twice)}\"");

            // (2) determinism: a second independent run is byte-identical
            var again = ShapeFilters.Apply(input, shape);
            Assert.True(once == again,
                $"Not deterministic for shape={shape} input=\"{Show(input)}\"");

            // (3) never grows
            Assert.True(once.Length <= input.Length,
                $"Output grew for shape={shape} input=\"{Show(input)}\": " +
                $"{input.Length} → {once.Length}");

            // (4) fail-open: a payload carrying ESC/BEL/CR comes back as the ORIGINAL instance
            if (input.AsSpan().IndexOfAny('\x1b', '\a', '\r') >= 0)
                Assert.Same(input, once);
        }
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Properties_NoLineVanishes(CommandShape shape)
    {
        // Not the minifier's subsequence law — a header is inserted and repeated fields are hoisted
        // out of their lines. What must still hold is that no line silently disappears: every
        // non-empty input line's own characters are still findable, in order, somewhere in the
        // output (its header comes before its entry, so order survives grouping).
        foreach (var input in AdversarialInputs())
        {
            var once = ShapeFilters.Apply(input, shape);
            if (ReferenceEquals(once, input))
                continue;

            foreach (var line in input.Split('\n'))
            {
                if (line.Length == 0)
                    continue;

                // PathList is the one shape that CONSUMES a character: the separator whose position
                // the header now records ("src/a.cs" → "src:" + "  a.cs"). Every other char survives.
                var reference = shape == CommandShape.PathList
                    ? line.Replace("/", string.Empty).Replace("\\", string.Empty)
                    : line;

                Assert.True(IsSubsequence(reference, once),
                    $"Line vanished for shape={shape} input=\"{Show(input)}\": " +
                    $"line=\"{Show(line)}\" output=\"{Show(once)}\"");
            }
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static bool IsSubsequence(string candidate, string reference)
    {
        var r = 0;
        foreach (var c in candidate)
        {
            while (r < reference.Length && reference[r] != c)
                r++;
            if (r == reference.Length)
                return false;
            r++;
        }
        return true;
    }

    /// <summary>Renders control characters visibly for assertion failure messages.</summary>
    private static string Show(string s)
    {
        if (s.Length > 120)
            s = s[..120] + "…";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\u001b' => "\\e",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\a' => "\\a",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
