using System;
using System.IO;

namespace MintLint;

/// <summary>
/// Decides whether a path names test code. Scoring uses it to leave out the measurements that
/// do not apply to tests (see <see cref="ScoringProfile.NotScoredInTests"/>), and VibeRails'
/// coverage rules use it to tell production code from tests, so there is one convention.
/// </summary>
public static class TestFileConvention
{
    private static readonly string[] StemSuffixes = ["Test", "Tests", "Spec", "Specs"];

    private static readonly string[] Directories = ["/test/", "/tests/", "/__tests__/", "/spec/", "/specs/"];

    /// <summary>
    /// True when the file name or one of its directories follows a test naming convention.
    /// </summary>
    /// <remarks>
    /// A test is named by a convention with a boundary, never by merely containing the letters:
    /// Contest.cs, Testimony.cs, LatestReport.cs and Inspector.cs are production code.
    /// <list type="bullet">
    /// <item>capitalized suffix: MyTest.cs, OrderTests.cs, ParserSpec.scala, ABTest.cs</item>
    /// <item>separator suffix: parser_test.go, user_spec.rb, calc-test.js</item>
    /// <item>dotted segment: my.test.js, calc.spec.ts</item>
    /// <item>prefix: test_math.py, TestOrders.java</item>
    /// <item>whole stem: test.py, tests.cs, spec.rb</item>
    /// <item>a test, tests, __tests__, spec or specs directory anywhere on the path</item>
    /// </list>
    /// </remarks>
    public static bool IsTestFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        return HasTestSuffix(stem) || HasTestSegmentOrPrefix(stem, Path.GetFileName(path).ToLowerInvariant()) || IsInTestDirectory(path);
    }

    // "MyTest" and "ABTest" end in a capitalized word, "parser_test" and "calc-test" set the word
    // off with a separator, and "tests" is the word itself; "Contest" and "Protest" are none of these.
    private static bool HasTestSuffix(string stem)
    {
        string lowerStem = stem.ToLowerInvariant();
        foreach (string suffix in StemSuffixes)
        {
            string lowerSuffix = suffix.ToLowerInvariant();
            bool capitalized = stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal);
            bool separated = lowerStem.EndsWith("_" + lowerSuffix, StringComparison.Ordinal) || lowerStem.EndsWith("-" + lowerSuffix, StringComparison.Ordinal);
            if (capitalized || separated || lowerStem == lowerSuffix)
            {
                return true;
            }
        }

        return false;
    }

    // "calc.spec.ts", "test_math.py" and "TestOrders.java".
    private static bool HasTestSegmentOrPrefix(string stem, string lowerName)
    {
        if (lowerName.Contains(".test.", StringComparison.Ordinal) || lowerName.Contains(".spec.", StringComparison.Ordinal))
        {
            return true;
        }

        if (lowerName.StartsWith("test_", StringComparison.Ordinal) || lowerName.StartsWith("tests_", StringComparison.Ordinal))
        {
            return true;
        }

        return stem.StartsWith("Test", StringComparison.Ordinal) && stem.Length > 4 && char.IsUpper(stem[4]);
    }

    // Test directories anywhere on the path, whichever separator the caller used. The leading
    // slash lets a repository-relative path that starts with the directory (tests/helpers.py,
    // spec/models/user.rb) match too.
    private static bool IsInTestDirectory(string path)
    {
        string normalized = "/" + path.Replace('\\', '/');
        foreach (string directory in Directories)
        {
            if (normalized.Contains(directory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
