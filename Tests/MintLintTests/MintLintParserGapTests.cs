using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// VIBE-174: parser gaps found while testing the nested-generic fix (VIBE-170 B3). None of them
/// needs a nested generic: a call in a field initializer read as a method, a field declaration
/// split at the commas of its own generic arguments, and Go type parameters hiding the function.
/// </summary>
public sealed class MintLintParserGapTests
{
    [Fact]
    public void CSharp_TargetTypedNewInFieldInitializer_IsNotAMethod()
    {
        // `= new();` ends like `Name();`, so it was taken for a bodyless method named `new`.
        const string source = """
            public sealed class Registry
            {
                private readonly Index _byName = new();

                public int Count() { return _byName.Size; }
            }
            """;

        FileMetrics metrics = Analyze("Registry.cs", source);

        Assert.DoesNotContain(metrics.Functions, function => function.Name == "new");
        ClassMetrics registry = Assert.Single(metrics.Classes);
        Assert.Equal(1, registry.MethodCount);
        Assert.Equal(1, registry.FieldCount);
        Assert.Equal(1, registry.Lcom4); // the stray `new` was a second, field-less component
    }

    [Fact]
    public void CSharp_CallsInInitializers_AreNotMethods()
    {
        // The same shape with a named call, an object initializer and a property initializer.
        const string source = """
            public sealed class Settings
            {
                private int _seed = Compute(3);
                private Options _options = new Options() { Retries = 2 };
                public string Name { get; } = Format("x");

                public int Seed() { return _seed + _options.Retries + Name.Length; }
            }
            """;

        FileMetrics metrics = Analyze("Settings.cs", source);

        Assert.Equal(["Seed"], MemberFunctionNames(metrics));
        ClassMetrics settings = Assert.Single(metrics.Classes);
        Assert.Equal(1, settings.MethodCount);
        Assert.Equal(3, settings.FieldCount);
    }

    [Fact]
    public void Java_GenericFieldType_IsOneField()
    {
        // The comma between String and List<Integer> split the declaration into two "fields".
        const string source = """
            public class Catalog {
                private Map<String, List<Integer>> index;
                private int size;

                public int count() { return index.size() + size; }
            }
            """;

        Assert.Equal(2, Assert.Single(Analyze("Catalog.java", source).Classes).FieldCount);
    }

    [Fact]
    public void Java_ComparisonInitializers_StillSplitAtTheirCommas()
    {
        // `lo < hi, full = hi > lo` is not a generic list: its `>` comes after the next `=`.
        const string source = """
            public class Range {
                private int lo, hi;
                private boolean empty = lo < hi, full = hi > lo;
            }
            """;

        Assert.Equal(4, Assert.Single(Analyze("Range.java", source).Classes).FieldCount);
    }

    [Fact]
    public void Java_CallInFieldInitializer_IsNotAMethod()
    {
        // The brace-family member scan had the C# initializer gap too: `new ArrayList<>()` became
        // a method named ArrayList, and the commas inside `max(a, b)` split off a field `b`.
        const string source = """
            public class Queue {
                private List<String> items = new ArrayList<>();
                private int limit = Math.max(MIN, MAX);

                public int size() { return items.size(); }
            }
            """;

        ClassMetrics queue = Assert.Single(Analyze("Queue.java", source).Classes);

        Assert.Equal(1, queue.MethodCount);
        Assert.Equal(2, queue.FieldCount);
    }

    [Fact]
    public void Cpp_EqualsAfterParametersOrInTemplate_KeepsTheMethod()
    {
        // The initializer cut must not drop a declaration whose `=` follows its parameters, or one
        // whose only `=` is a default template argument.
        const string source = """
            class Shape {
            public:
                virtual double area() const = 0;
                template <typename T = int> T scaled(T factor) const;
                std::map<int, std::vector<int>> cache;
            };
            """;

        ClassMetrics shape = Assert.Single(Analyze("shape.cpp", source).Classes);

        Assert.Equal(2, shape.MethodCount);
        Assert.Equal(1, shape.FieldCount);
    }

    [Fact]
    public void Go_FunctionWithTypeParameters_IsDetected()
    {
        // The name lookup before `(` stopped at the `]` of the type parameter list.
        const string source = """
            package util

            func Keys[K comparable, V any](m map[K]V) []K {
                keys := make([]K, 0, len(m))
                for k := range m {
                    keys = append(keys, k)
                }
                return keys
            }

            func Map[T, U any](s []T, f func(T) U) (out []U, err error) {
                if s == nil { return nil, nil }
                return out, nil
            }
            """;

        FileMetrics metrics = Analyze("util.go", source);

        FunctionMetrics keys = Function(metrics, "Keys");
        Assert.Equal(2, keys.Cyclomatic);
        Assert.Equal(1, keys.ParameterCount);
        Assert.Equal(2, Function(metrics, "Map").ParameterCount);
        Assert.Equal(0, Function(metrics, "global").Cyclomatic); // their decisions are no longer charged here
    }

    [Fact]
    public void Go_GenericReceiverMethod_KeepsItsOwner()
    {
        const string source = """
            package util

            type Set[T comparable] struct {
                items map[T]bool
            }

            func (s *Set[T]) Has(v T) bool {
                if s.items == nil { return false }
                return s.items[v]
            }
            """;

        ClassMetrics set = Assert.Single(Analyze("set.go", source).Classes);

        Assert.Equal("Set", set.Name);
        Assert.Equal(1, set.MethodCount);
    }

    private static FileMetrics Analyze(string path, string source)
    {
        return Assert.Single(MintLintAnalyzer.AnalyzeSources([new SourceInput(path, source)]));
    }

    private static FunctionMetrics Function(FileMetrics metrics, string name)
    {
        return Assert.Single(metrics.Functions, function => function.Name == name);
    }

    private static string[] MemberFunctionNames(FileMetrics metrics)
    {
        return [.. metrics.Functions.Where(function => function.Name != "global").Select(function => function.Name)];
    }
}
