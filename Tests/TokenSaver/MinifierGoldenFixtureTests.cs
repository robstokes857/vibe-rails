using System.Runtime.CompilerServices;
using TokenSaver.Minify;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Golden-corpus tests: realistic captured-style tool outputs under <c>Fixtures\</c>, each a
/// <c>&lt;name&gt;.input.txt</c> / <c>&lt;name&gt;.expected.txt</c> pair. Expected files were
/// derived BY HAND from <see cref="OutputMinifier"/>'s transform rules, not by running the
/// minifier — they pin behavior, they don't echo it. Regenerating one from actual output defeats
/// the entire point of the file.
///
/// Fixtures contain REAL control bytes (0x1B, 0x0D, 0x07) and deliberate \r / \r\n framing, so
/// they are read raw via <see cref="File.ReadAllText(string)"/> (UTF-8, no BOM, and .NET performs
/// no newline translation). Never open them with anything that normalizes line endings, and keep
/// <c>.gitattributes</c>-style autocrlf munging away from them.
///
/// The files are read from the SOURCE tree via [CallerFilePath] (same pattern as
/// SessionParseV4FixtureTests), so nothing needs to be copied to the output directory.
/// </summary>
public class MinifierGoldenFixtureTests
{
    private static readonly string FixtureDir = GetFixtureDir();

    private static string GetFixtureDir([CallerFilePath] string? callerPath = null)
        => Path.Combine(Path.GetDirectoryName(callerPath)!, "Fixtures");

    public static TheoryData<string> FixtureNames()
    {
        const string suffix = ".input.txt";
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(FixtureDir, "*" + suffix)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path)[..^suffix.Length]);
        }
        return data;
    }

    [Fact]
    public void Corpus_IsPresent_AndEveryInputHasAnExpected()
    {
        Assert.True(Directory.Exists(FixtureDir), $"Missing fixture dir: {FixtureDir}");

        var inputs = Directory.GetFiles(FixtureDir, "*.input.txt");
        Assert.True(inputs.Length >= 8, $"Expected at least 8 fixture pairs, found {inputs.Length}");

        foreach (var input in inputs)
        {
            var expected = input[..^".input.txt".Length] + ".expected.txt";
            Assert.True(File.Exists(expected), $"Missing expected file for {Path.GetFileName(input)}");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_MinifiesToExpected(string name)
    {
        var input = File.ReadAllText(Path.Combine(FixtureDir, name + ".input.txt"));
        var expected = File.ReadAllText(Path.Combine(FixtureDir, name + ".expected.txt"));

        var actual = OutputMinifier.Minify(input, MinifyFlags.Default);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_IsIdempotentAndDeterministic(string name)
    {
        var input = File.ReadAllText(Path.Combine(FixtureDir, name + ".input.txt"));

        var once = OutputMinifier.Minify(input, MinifyFlags.Default);
        var again = OutputMinifier.Minify(input, MinifyFlags.Default);
        var twice = OutputMinifier.Minify(once, MinifyFlags.Default);

        Assert.Equal(once, again); // deterministic: independent runs are byte-identical
        Assert.Equal(once, twice); // idempotent: re-minifying the output is a no-op
    }
}
