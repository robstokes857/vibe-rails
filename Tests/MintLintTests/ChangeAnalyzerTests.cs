using System;
using System.Collections.Generic;
using System.Linq;
using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// What a change is graded on (<see cref="ChangeAnalyzer"/>): the lines it added, for size,
/// dependencies, duplication and types; each function it created, whole and at its real line; what
/// it made worse in a function that existed; and the top-level code it added. The fixtures are small
/// enough to count by hand: <c>Legacy</c> has eleven <c>if</c> statements, so its cyclomatic
/// complexity is 12, past the warning limit of 10.
/// </summary>
public sealed class ChangeAnalyzerTests
{
    // Normalized, so the fixture reads the same whatever line endings this file is checked out with.
    private static readonly string Before = BeforeSource.Replace("\r\n", "\n");

    private const string BeforeSource = """
        using System;

        class Orders
        {
            int Total(int a, int b)
            {
                if (a > 0) return a;
                if (b > 0) return b;
                return 0;
            }

            int Legacy(int x)
            {
                if (x == 1) return 1;
                if (x == 2) return 2;
                if (x == 3) return 3;
                if (x == 4) return 4;
                if (x == 5) return 5;
                if (x == 6) return 6;
                if (x == 7) return 7;
                if (x == 8) return 8;
                if (x == 9) return 9;
                if (x == 10) return 10;
                if (x == 11) return 11;
                return 0;
            }
        }
        """;

    [Fact]
    public void AFunctionTheChangeOnlyTouched_IsNotGraded()
    {
        // One statement added inside Legacy, no new branch: its complexity is still 12, and it
        // was 12 before. The change did not make it worse, so it is not this change's finding.
        // The added line is inside a function, so there is no top-level code either.
        string after = Before.Replace("        if (x == 1) return 1;", "        var seen = x;\n        if (x == 1) return 1;");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Orders.cs", Before, after)]);

        FileMetrics file = Assert.Single(analysis.Files);
        Assert.Empty(file.Functions);
        Assert.Equal(1, file.Loc); // only the added line is measured for size
        Assert.Empty(MintLintScorer.Score(file).Findings);
        // The whole file is still available, with Legacy as it stands.
        Assert.Equal(12, Assert.Single(analysis.WholeFiles).Functions.Single(function => function.Name == "Legacy").Cyclomatic);
    }

    [Fact]
    public void AFunctionTheChangeMadeWorse_IsGradedWhole_AtItsRealLine()
    {
        // One more branch in Legacy: 12 becomes 13. Measured alone, the added line is a single
        // `if` with no name and no line. Measured as the function it belongs to, it is a
        // function past its limit that this change made worse.
        string after = Before.Replace("        return 0;\n    }\n}", "        if (x == 12) return 12;\n        return 0;\n    }\n}");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Orders.cs", Before, after)]);

        FileMetrics file = Assert.Single(analysis.Files);
        FunctionMetrics legacy = Assert.Single(file.Functions);
        Assert.Equal(("Legacy", 13, LineOf(after, "int Legacy(int x)")), (legacy.Name, legacy.Cyclomatic, legacy.Line));
        Assert.Equal(12, analysis.Baselines["Orders.cs"][("Legacy", legacy.Line)].Cyclomatic);

        // Twelve independent branches are also 2^12 = 4,096 paths, past NPath's warning limit of
        // 1,000. Cyclomatic lists first: it is further past its limit (3 of 10 against 3,096 of 99,000).
        FileScore score = MintLintScorer.Score(file);
        Assert.Equal(
            [("cyclomatic_complexity", 13.0, "warning", "Legacy", (int?)legacy.Line), ("npath_complexity", 4096.0, "warning", "Legacy", (int?)legacy.Line)],
            score.Findings.Select(finding => (finding.Name, finding.Value, finding.Level, finding.Source, finding.Line)));
        // 13 of 10/20 → Complexity 65 → 15 + 15 / 50 × 45 = 28.5: a C, where the added line alone graded A.
        Assert.Equal(28.5, score.Overall.Score);
    }

    [Fact]
    public void ANewFunction_IsGradedWhole_AndTheOneItWasSplitFrom_IsNot()
    {
        // A refactor: the last six branches move out of Legacy into a new function. Legacy
        // drops from 12 to 7 (improved, so not graded); the new function is graded as itself.
        string after = Before
            .Replace("        if (x == 6) return 6;\n        if (x == 7) return 7;\n        if (x == 8) return 8;\n        if (x == 9) return 9;\n        if (x == 10) return 10;\n        if (x == 11) return 11;\n        return 0;\n    }\n}",
                "        return Upper(x);\n    }\n\n    int Upper(int x)\n    {\n        if (x == 6) return 6;\n        if (x == 7) return 7;\n        if (x == 8) return 8;\n        if (x == 9) return 9;\n        if (x == 10) return 10;\n        if (x == 11) return 11;\n        return 0;\n    }\n}");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Orders.cs", Before, after)]);

        FileMetrics file = Assert.Single(analysis.Files);
        FunctionMetrics upper = Assert.Single(file.Functions);
        Assert.Equal(("Upper", 7, LineOf(after, "int Upper(int x)")), (upper.Name, upper.Cyclomatic, upper.Line));
        // Upper kept lines that were Legacy's, but Legacy is still there: Upper is not Legacy renamed.
        Assert.DoesNotContain(analysis.Baselines["Orders.cs"].Keys, key => key.Name == "Upper");
        // Upper is inside its limit (7 of 10 → Complexity 35, dampened to 10.5): grade B, nothing to fix.
        FileScore score = MintLintScorer.Score(file);
        Assert.Empty(score.Findings);
        Assert.Equal(10.5, score.Overall.Score);
        Assert.Equal('B', score.Overall.Grade);
    }

    [Fact]
    public void ANewFile_IsMeasuredAsTheFile()
    {
        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([new ChangedSource("Orders.cs", Before)]);

        FileMetrics changed = Assert.Single(analysis.Files);
        FileMetrics whole = Assert.Single(MintLintAnalyzer.AnalyzeSources([new SourceInput("Orders.cs", Before)]));
        Assert.Equal(whole.Functions, changed.Functions);
        Assert.Equal(whole.Loc, changed.Loc);
        Assert.Same(changed, Assert.Single(analysis.WholeFiles));
        Assert.Empty(analysis.Baselines);
    }

    [Fact]
    public void ATypeFoundInTheAddedLines_KeepsItsLineInTheFile()
    {
        string after = Before + "\n\nclass Shipping\n{\n    int Cost(int weight)\n    {\n        return weight * 2;\n    }\n}";

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Orders.cs", Before, after)]);

        FileMetrics file = Assert.Single(analysis.Files);
        ClassMetrics shipping = Assert.Single(file.Classes);
        Assert.Equal(("Shipping", LineOf(after, "class Shipping")), (shipping.Name, shipping.Line));
        // The type's own lines (its header and braces) are outside every function: top-level code, with no branch in it.
        FunctionMetrics cost = Assert.Single(file.Functions, function => function.Name != "global");
        Assert.Equal(("Cost", LineOf(after, "int Cost(int weight)")), (cost.Name, cost.Line));
        Assert.Equal(0, file.Functions.Single(function => function.Name == "global").Cyclomatic);
    }

    [Fact]
    public void AFunctionIsGradedOnlyOnWhatTheChangeMadeWorse()
    {
        // Wide has five parameters (past the limit of four) and always had. The change adds a
        // branch: its complexity goes from 2 to 3, still far inside its limit. The parameter
        // count is inherited, so it reads 0 and is not this change's finding.
        const string before = "class W\n{\n    int Wide(int a, int b, int c, int d, int e)\n    {\n        if (a > 0) return b;\n        return c + d + e;\n    }\n}";
        string after = before.Replace("        if (a > 0) return b;", "        if (a > 0) return b;\n        if (b > 0) return c;");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("W.cs", before, after)]);

        FileMetrics file = Assert.Single(analysis.Files);
        FunctionMetrics wide = Assert.Single(file.Functions);
        Assert.Equal(("Wide", 3, 0, 3), (wide.Name, wide.Cyclomatic, wide.ParameterCount, wide.Line));
        Assert.Equal(0, file.MaxParameterCount);
        Assert.Empty(MintLintScorer.Score(file).Findings);
        // Measured whole, the same function does have that finding.
        Assert.Contains(MintLintScorer.Score(Assert.Single(analysis.WholeFiles)).Findings, finding => finding.Name == "parameter_count" && finding.Source == "Wide");
        Assert.Equal(5, analysis.Baselines["W.cs"][("Wide", 3)].ParameterCount);
    }

    [Fact]
    public void TopLevelCode_IsMeasuredOnTheAddedLinesAlone()
    {
        // A script with six top-level branches. Top-level code is not a function, and in a long
        // script or spec file it only grows, so it is graded on what the change adds to it.
        string script = "import os\n\n" + string.Concat(Enumerable.Range(0, 6).Select(i => $"if os.name == 'os{i}':\n    print({i})\n"))
            + "\ndef run(value):\n    return value\n";

        // A new import: top-level code with no branch in it.
        ChangeAnalysis import = ChangeAnalyzer.Analyze([Change("tool.py", script, script.Replace("import os\n", "import os\nimport sys\n"))]);
        Assert.Equal(0, Assert.Single(Assert.Single(import.Files).Functions).Cyclomatic);

        // One new top-level branch: one decision point, whatever the six that were already there.
        ChangeAnalysis branch = ChangeAnalyzer.Analyze([Change("tool.py", script, script.Replace("\ndef run", "if os.name == 'posix':\n    print('posix')\n\ndef run"))]);
        FunctionMetrics topLevel = Assert.Single(Assert.Single(branch.Files).Functions);
        Assert.Equal(("global", 1), (topLevel.Name, topLevel.Cyclomatic));
        Assert.Equal(6, Assert.Single(branch.WholeFiles).Functions.Single(function => function.Name == "global").Cyclomatic - 1);
        Assert.Empty(branch.Baselines["tool.py"]); // top-level code has no "before", and run was not touched

        // A line added inside run is part of run, not top-level code: nothing is top-level here.
        ChangeAnalysis inside = ChangeAnalyzer.Analyze([Change("tool.py", script, script.Replace("    return value\n", "    value = value or 1\n    return value\n"))]);
        Assert.DoesNotContain(Assert.Single(inside.Files).Functions, function => function.Name == "global");
    }

    [Fact]
    public void Overloads_AreToldApartByTheLinesTheyKept()
    {
        // Two overloads named Pick, cyclomatic 2 and 4. The first gains a branch: 2 becomes 3.
        // It kept its own declaration line, so it is compared with itself (made worse), not
        // passed as "no worse than a Pick was".
        const string before = "class P\n{\n    int Pick(int a)\n    {\n        if (a > 0) return 1;\n        return 0;\n    }\n\n    int Pick(int a, int b)\n    {\n        if (a > 0) return 1;\n        if (b > 0) return 2;\n        if (a > b) return 3;\n        return 0;\n    }\n}";
        string after = before.Replace("        if (a > 0) return 1;\n        return 0;", "        if (a > 0) return 1;\n        if (a < 0) return 2;\n        return 0;");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("P.cs", before, after)]);

        FunctionMetrics pick = Assert.Single(Assert.Single(analysis.Files).Functions);
        Assert.Equal(("Pick", 3, 3), (pick.Name, pick.Cyclomatic, pick.Line));
        Assert.Equal(2, analysis.Baselines["P.cs"][("Pick", 3)].Cyclomatic);
    }

    [Fact]
    public void ANewOverload_IsNew_BesideTheOneThatWasThere()
    {
        // Pick(int) has four branches. The change adds Pick(int, int) with 20: cyclomatic 21.
        // The first Pick kept its lines, so the new one is not it. It is new, and graded whole.
        const string before = "class P\n{\n    int Pick(int a)\n    {\n        if (a == 1) return 1;\n        if (a == 2) return 2;\n        if (a == 3) return 3;\n        if (a == 4) return 4;\n        return 0;\n    }\n}";
        string overload = "\n    int Pick(int a, int b)\n    {\n"
            + string.Concat(Enumerable.Range(1, 20).Select(i => $"        if (b == {i}) return {i};\n"))
            + "        return a;\n    }\n";
        string after = before.Replace("        return 0;\n    }\n}", "        return 0;\n    }\n" + overload + "}");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("P.cs", before, after)]);

        FunctionMetrics pick = Assert.Single(Assert.Single(analysis.Files).Functions, function => function.Name == "Pick");
        Assert.Equal((21, LineOf(after, "int Pick(int a, int b)")), (pick.Cyclomatic, pick.Line));
        Assert.Empty(analysis.Baselines["P.cs"]);
    }

    [Fact]
    public void ANewFunction_IsGradedWhole_WhateverAnotherOfItsNameMeasured()
    {
        // Existing.Run has 22 branches. The change adds a second type with a Run of its own:
        // 20 branches, cyclomatic 21, past the critical limit of 20. It is a new function. That
        // an unrelated Run in the same file was already worse is no reason to pass it.
        string before = TypeWithRun("Existing", 22);
        string after = before + "\n\n" + TypeWithRun("Added", 20);

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Jobs.cs", before, after)]);

        FileMetrics file = Assert.Single(analysis.Files);
        FunctionMetrics run = Assert.Single(file.Functions, function => function.Name == "Run");
        Assert.Equal((21, LineOf(after, "class Added") + 2), (run.Cyclomatic, run.Line));
        Assert.Empty(analysis.Baselines["Jobs.cs"]);
        FileScore score = MintLintScorer.Score(file);
        Assert.Contains(score.Findings, finding =>
            finding.Name == "cyclomatic_complexity" && finding.Level == "critical" && finding.Source == "Run" && finding.Line == run.Line);
        Assert.Equal('F', score.Overall.Grade);
    }

    [Fact]
    public void AFunctionMadeWorse_IsComparedWithTheFunctionItWas_NotWithAnotherOfItsName()
    {
        // Two types, each with a Run: one with 22 branches, one with none. The change gives the
        // second 20 branches, so it goes from 1 to 21. Every line it kept also occurs in the
        // other Run, so no line says which it was; the other Run kept its own lines, which
        // leaves this one as the Run nobody else was.
        string before = TypeWithRun("Existing", 22) + "\n\n" + TypeWithRun("Added", 0);
        string after = TypeWithRun("Existing", 22) + "\n\n" + TypeWithRun("Added", 20);

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Jobs.cs", before, after)]);

        FunctionMetrics run = Assert.Single(Assert.Single(analysis.Files).Functions);
        Assert.Equal((21, LineOf(after, "class Added") + 2), (run.Cyclomatic, run.Line));
        Assert.Equal(1, analysis.Baselines["Jobs.cs"][("Run", run.Line)].Cyclomatic);
    }

    [Fact]
    public void ARenamedFunction_IsStillTheFunctionItWas()
    {
        // Legacy becomes Lookup and nothing else changes. Its twelve paths were there before the
        // change, under the old name, so they are not this change's finding.
        string after = Before.Replace("int Legacy(int x)", "int Lookup(int x)");

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Orders.cs", Before, after)]);

        Assert.Empty(Assert.Single(analysis.Files).Functions);
        FunctionMetrics was = analysis.Baselines["Orders.cs"][("Lookup", LineOf(after, "int Lookup(int x)"))];
        Assert.Equal(("Legacy", 12), (was.Name, was.Cyclomatic));
    }

    [Fact]
    public void WhenEveryLineChanged_AFunctionIsTheOneOfItsName()
    {
        // The file is indented two more columns: every line is an added line and no function
        // kept one. Each is still the function of its name, so nothing is this change's finding.
        string after = string.Join("\n", Before.Split('\n').Select(line => line.Length == 0 ? line : "  " + line));

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("Orders.cs", Before, after)]);

        Assert.DoesNotContain(Assert.Single(analysis.Files).Functions, function => function.Name != "global");
        Assert.Equal(
            [("Legacy", 12), ("Total", 3)],
            analysis.Baselines["Orders.cs"].Select(pair => (pair.Key.Name, pair.Value.Cyclomatic)).Order());
    }

    [Fact]
    public void ANewAnonymousFunction_IsNew_AmongOthersOfItsName()
    {
        // A spec file: every test body is an anonymous function with the same name. A new test
        // is a new function, graded whole, whatever the tests before it measure.
        string before = Spec("one", 12) + "\n\n" + Spec("two", 1);
        string after = before + "\n\n" + Spec("three", 11);

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([Change("orders.test.js", before, after)]);

        FunctionMetrics added = Assert.Single(Assert.Single(analysis.Files).Functions, function => function.Name != "global");
        Assert.Equal((12, LineOf(after, "test('three'")), (added.Cyclomatic, added.Line));
        Assert.Empty(analysis.Baselines["orders.test.js"]);
    }

    [Fact]
    public void WithoutLineNumbers_TheAddedLinesAreMeasuredAlone()
    {
        // A caller with a patch but no line map cannot place the lines; they are graded as text.
        const string added = "class More\n{\n    int Extra(int a)\n    {\n        if (a > 0) return 1;\n        return 0;\n    }\n}\n";

        ChangeAnalysis analysis = ChangeAnalyzer.Analyze([new ChangedSource("Orders.cs", Before + "\n" + added, added)]);

        FileMetrics file = Assert.Single(analysis.Files);
        Assert.Equal(3, file.Functions.Single(function => function.Name == "Extra").Line); // a line of the fragment, not of the file
        Assert.Contains(Assert.Single(analysis.WholeFiles).Functions, function => function.Name == "Legacy");
    }

    [Fact]
    public void SkipsUnsupportedAndEmptyChanges_AndKeepsTheFirstOfARepeatedPath()
    {
        ChangeAnalysis analysis = ChangeAnalyzer.Analyze(
        [
            new ChangedSource("notes.md", "# notes"),
            new ChangedSource("Removed.cs", "class Removed { }", "", []),
            new ChangedSource("./a.cs", "class First { }"),
            new ChangedSource("a.cs", "class Second { }"),
        ]);

        FileMetrics only = Assert.Single(analysis.Files);
        Assert.Equal(("a.cs", "First"), (only.File, Assert.Single(only.Classes).Name));
        Assert.Throws<ArgumentNullException>(() => ChangeAnalyzer.Analyze(null!));
        Assert.Throws<ArgumentException>(() => ChangeAnalyzer.Analyze([null!]));
    }

    /// <summary>A type with one method, Run, of <paramref name="branches"/> if statements: cyclomatic complexity one more.</summary>
    private static string TypeWithRun(string type, int branches) =>
        $"class {type}\n{{\n    int Run(int x)\n    {{\n"
        + string.Concat(Enumerable.Range(1, branches).Select(i => $"        if (x == {i}) return {i};\n"))
        + "        return 0;\n    }\n}";

    /// <summary>A JavaScript test whose body has <paramref name="branches"/> if statements.</summary>
    private static string Spec(string name, int branches) =>
        $"test('{name}', () => {{\n"
        + string.Concat(Enumerable.Range(1, branches).Select(i => $"    if (value === {i}) expect(value).toBe({i});\n"))
        + "});";

    /// <summary>The 1-based line of the first line containing <paramref name="text"/>.</summary>
    private static int LineOf(string source, string text)
    {
        string[] lines = source.Split('\n');
        int index = Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));
        Assert.True(index >= 0, $"'{text}' is not in the fixture.");
        return index + 1;
    }

    /// <summary>
    /// A change as Git's zero-context patch reports it, for fixtures that edit one place: the lines of
    /// <paramref name="after"/> between what the two texts share at the start and at the end, with
    /// their line numbers.
    /// </summary>
    private static ChangedSource Change(string path, string before, string after)
    {
        string[] old = before.Split('\n');
        string[] now = after.Split('\n');
        int start = 0;
        while (start < old.Length && start < now.Length && old[start] == now[start])
        {
            start++;
        }

        int shared = Math.Min(old.Length, now.Length) - start;
        int end = 0;
        while (end < shared && old[^(end + 1)] == now[^(end + 1)])
        {
            end++;
        }

        int count = now.Length - end - start;
        return new ChangedSource(path, after, string.Join("\n", now.Skip(start).Take(count)), Enumerable.Range(start + 1, count).ToArray(), before);
    }
}
