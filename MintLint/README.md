# MintLint

MintLint measures and grades maintainability signals for supported source languages.
The metric thresholds and weights live in [ScoringProfile](Scoring/ScoringProfile.cs).
[ScoreCalculator](Scoring/ScoreCalculator.cs) implements category aggregation, the file
roll-up, letter grades and ratings.

## What a grade means

- A measurement past its warning limit is a **problem**; at its critical limit it is
  critical. Each pair of limits marks roughly the worst tenth and the worst few percent
  of real functions, types or files (the journal has the distributions).
- **One critical measurement fails the file.** A full-weight category at its critical
  limit scores 60 concern: health 40, grade F. Each further category at its critical
  limit raises the score, so a file that is wrong four ways outranks one wrong one way.
- **Nothing past a limit means A or B.** A grade of C or lower always comes with a
  finding, so a failed minimum grade can always say what to change.
- Health is 100 minus concern: A from 90, B from 80, C from 70, D from 55, otherwise F.
  Ratings follow the grade: Clean (A, B), Okay (C), NeedsWork (D), AtRisk (F).
  `OverallScore.Grade` and `ScoreCalculator.HealthFor` are the one table; VibeRails'
  `Code quality minimum` rule and lane checks read it.
- The overall grade of a scan is the mean of its files **weighted by lines of code**.
- Test files ([TestFileConvention](TestFileConvention.cs)) are not graded on cohesion or
  on hard-coded and ambient dependencies (`ScoringProfile.NotScoredInTests`).
- `FileScore.Findings` lists every function, type and file-level measurement past a
  limit, one per declaration, worst first. A category keeps only the worst value of each
  metric; the findings name each declaration to fix.

Why those numbers are what they are is the tuning journal, kept in the private
`vibe-books` repository at
[mintlint/MintLintAlgo.md](../../vibe-books/mintlint/MintLintAlgo.md) with sibling
checkouts. Read it before changing grading and record the change there: the
aggregation knobs are tuned against hand-computed fixture values in
`Tests/MintLintTests`, so a change that looks local to `ScoreCalculator` moves
numbers the tests assert by hand.

## Change-scoped grading

Git Guard, the `Code quality minimum` rule, lane checks and the Code quality changes scan
grade a change, not whole files. [ChangeAnalyzer](ChangeAnalyzer.cs) decides what is
measured, from each file's current text, the lines the change added with their line
numbers, and the text before:

1. VibeRails asks Git for a zero-context patch for the staged, working-tree, unpushed or
   committed comparison.
2. **Added lines** are measured for size, dependencies, duplication, the maintainability
   index and types. Removed lines are never scored; a removal-only edit produces no
   file score. New files are entirely added and are analyzed in full.
3. **A function the change created** is measured whole, at its real line.
4. **A function that existed** and that the change touched is measured whole for what the
   change made worse: a cyclomatic, cognitive or NPath complexity, nesting depth or
   parameter count higher than that function had. Everything it inherited or improved
   reads 0, and a function the change made no worse is not graded.
5. **Added lines outside every function** (imports, fields, script statements) are
   measured on their own, as top-level code.

**Which function a function was** is decided by the lines it kept, not by its name alone
([FunctionHistory](FunctionHistory.cs)): methods of different types, overloads and anonymous
functions share names, and comparing by name let a complex new `Run` pass because another
type's `Run` was already worse.

1. A function that kept lines of a function of its own name was that function. Only a line
   whose text occurs once in the previous version counts, because only that line is certain
   to be the same line.
2. A function that kept lines only of a function whose name is gone from the file is that
   function renamed, so a rename does not turn inherited complexity into a new finding.
3. A function left over takes the first function of its name that nobody kept lines of, in
   file order: first the ones that kept some line, then the ones the change wrote every line
   of. This covers a rewrite and a file whose every line changed (re-indented, new line
   endings).
4. Any other function is new.

Two cases are decided by rule, because nothing in the text tells them apart. A function
deleted and another of the same name added in one change are compared with each other, which
is lenient. A helper extracted in place (its body lines unchanged, under a new name, with the
original still there) is new and graded whole, like a helper extracted anywhere else.

Lines added inside an existing function are a fragment of it. Measured alone they have
no name, no line and no meaning: until VIBE-274 several such fragments were graded as
one lump of top-level code, and a refactor that split one function into several failed
for it. A caller without line numbers still gets that older behavior (the added lines
measured as text).

This changes **which measurements enter grading**, not the metric formulas. The
standalone `MintLintAnalyzer.AnalyzePath` and `AnalyzeFile` APIs continue to analyze
complete files.

Added-line fragments may have unmatched delimiters. Metric traversal must keep moving
forward through incomplete blocks; in particular, an unclosed `switch`, `match`, or
`select` body must never reset the scan position to its missing closing brace. Source-input
regression tests cover these fragments, including PowerShell `[switch]` parameters.

## Declaration and parameter parsing

C# member signatures stop before field initializers, expression bodies, and generic
constraints. Expression-bodied properties count as fields, while generic methods keep
their declared names even with `where T : new()` constraints. Java annotations with
arguments do not count as methods.

Parameter counts exclude commas inside generic type arguments, including nested lists
whose closing tokens are `>>` or `>>>`. Generic calls in default expressions are also
recognized; comparison operators in defaults preserve parameter separators. These cases
are covered by `Tests/MintLintTests/MintLintParserGapTests.cs`.

---

*Last checked: 2026-08-06T18:21:17Z by opencode (glm-5.2)*
