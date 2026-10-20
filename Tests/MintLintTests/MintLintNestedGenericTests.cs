using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// VIBE-170 B3: the tokenizer reads <c>&gt;&gt;</c> and <c>&gt;&gt;&gt;</c> as single shift operators, so a
/// nested generic such as <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c> ends in one token that closes
/// two argument lists. The brace-family parser (Go, Rust, C, C++, Java) counted only <c>&gt;</c>,
/// so the list never closed and the declaration was read past its own body. C# has its own parser.
/// </summary>
public sealed class MintLintNestedGenericTests
{
    [Fact]
    public void Java_NestedGenericReturnType_EndsAtItsOwnBody()
    {
        // Unclosed, the return type's angle search ran on into big's comparisons, so the member
        // scan skipped ahead and big was found a second time by the fallback scan.
        const string source = """
            public class Box {
                private int size;

                public Map<String, List<Integer>> all() { return null; }

                public boolean big() { return size > 3 && size > 4; }

                public void grow() { size++; }
            }
            """;

        FileMetrics metrics = Analyze("Box.java", source);

        Assert.Equal(3, Assert.Single(metrics.Classes).MethodCount);
        Assert.Equal(2, Function(metrics, "big").Cyclomatic);
    }

    [Fact]
    public void Cpp_NestedGenericQualifier_NamesTheOwningClass()
    {
        // The owner sits before the `<` that `>>` closes; without the walk back, size had no owner.
        const string source = """
            template <typename T>
            class Cache {
            public:
                int size();
                int count;
            };

            int Cache<std::vector<int>>::size() { return count > 1 ? 1 : 0; }
            """;

        ClassMetrics cache = Assert.Single(Analyze("cache.cpp", source).Classes);

        Assert.Equal(2, cache.MethodCount);
        Assert.Equal(3, cache.Wmc);
    }

    [Fact]
    public void Rust_TripleCloseBeforeParameters_KeepsTheFunction()
    {
        // `>>>` sits right before `(`: the name lookup has to walk back through all three lists.
        const string source = """
            fn load<T: AsRef<Vec<u8>>>(value: T) -> usize {
                if value.as_ref().len() > 3 { return 1; }
                0
            }

            fn plain(x: u32) -> u32 { x + 1 }
            """;

        FileMetrics metrics = Analyze("lib.rs", source);

        FunctionMetrics load = Function(metrics, "load");
        Assert.Equal(2, load.Cyclomatic);
        Assert.Equal(1, load.ParameterCount);
        Assert.Equal(0, Function(metrics, "global").Cyclomatic); // load's `if` is no longer charged here
    }

    private static FileMetrics Analyze(string path, string source)
    {
        return Assert.Single(MintLintAnalyzer.AnalyzeSources([new SourceInput(path, source)]));
    }

    private static FunctionMetrics Function(FileMetrics metrics, string name)
    {
        return Assert.Single(metrics.Functions, function => function.Name == name);
    }
}
