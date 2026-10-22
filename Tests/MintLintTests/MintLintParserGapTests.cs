using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// VIBE-174/VIBE-175: regression coverage for declaration boundaries, annotations, generic
/// parameter lists, and Go type parameters found while testing the nested-generic fix.
/// </summary>
public sealed class MintLintParserGapTests
{
    [Fact]
    public void CSharp_ExpressionBodiedPropertyCalls_AreFields()
    {
        const string source = """
            public sealed class Person
            {
                private int _x;
                public string Name => Format(_x);
                public string Label => Format(_x).Trim();
                public string Describe(int count) => Format(count);
            }
            """;

        FileMetrics metrics = Analyze("Person.cs", source);

        Assert.Equal(["Describe"], MemberFunctionNames(metrics));
        Assert.Equal(1, Function(metrics, "Describe").ParameterCount);
        ClassMetrics person = Assert.Single(metrics.Classes);
        Assert.Equal(1, person.MethodCount);
        Assert.Equal(3, person.FieldCount);
    }

    [Theory]
    [InlineData("T Create<T>() where T : new();", "Create", 0)]
    [InlineData("public T Make<T>(int count) where T : new() { return new T(); }", "Make", 1)]
    [InlineData("public T Build<T, U>(U value, int count) where T : class, new() where U : new() => new T();", "Build", 2)]
    [InlineData("public T Convert<T, U>(U value) { return default; }", "Convert", 1)]
    [InlineData("public T Identity<T>(T value) => value;", "Identity", 1)]
    [InlineData("public void where(int value) { }", "where", 1)]
    [InlineData("public T where<T>(int value) where T : new() { return new T(); }", "where", 1)]
    public void CSharp_GenericConstraints_KeepTheDeclaredMethod(string declaration, string name, int parameters)
    {
        FileMetrics metrics = Analyze("Factory.cs", "public class Factory { " + declaration + " }");

        Assert.Equal([name], MemberFunctionNames(metrics));
        Assert.Equal(parameters, Function(metrics, name).ParameterCount);
        ClassMetrics factory = Assert.Single(metrics.Classes);
        Assert.Equal(1, factory.MethodCount);
        Assert.Equal(0, factory.FieldCount);
    }

    [Theory]
    [InlineData("@Column(length = 10)")]
    [InlineData("@persistence.Column(length = 10, nullable = false)")]
    [InlineData("@Indexed @Column(length = 10)")]
    [InlineData("private @Column(length = 10)")]
    public void Java_AnnotationArguments_DoNotDeclareMethods(string annotation)
    {
        string source = """
            public class Article {
            ANNOTATION String title;
                @Size(max = 10) public String format(int width) { return title; }
                @Size(max = 10) public abstract String shorten(int width, int height);
            }
            """.Replace("ANNOTATION", annotation);

        FileMetrics metrics = Analyze("Article.java", source);

        Assert.Equal(["format", "shorten"], MemberFunctionNames(metrics));
        Assert.Equal(1, Function(metrics, "format").ParameterCount);
        Assert.Equal(2, Function(metrics, "shorten").ParameterCount);
        ClassMetrics article = Assert.Single(metrics.Classes);
        Assert.Equal(2, article.MethodCount);
        Assert.Equal(1, article.FieldCount);
    }

    [Theory]
    [InlineData("Generic.cs", "class C { void Put(Dictionary<string, int> m) {} }", "Put", 1)]
    [InlineData("Generic.java", "class C { void put(Map<String, Integer> m) {} }", "put", 1)]
    [InlineData("Nested.cs", "class C { void Put(Dictionary<string, List<int>> m, int n) {} }", "Put", 2)]
    [InlineData("Nested.java", "class C { void put(Map<String, Map<Integer, List<Long>>> m, int n) {} }", "put", 2)]
    [InlineData("Nested.cpp", "void put(std::map<int, std::pair<int, int>> m, int n) {}", "put", 2)]
    [InlineData("nested.rs", "fn put(m: Map<String, Vec<Pair<i32, i32>>>, n: i32) {}", "put", 2)]
    [InlineData("nested.ts", "function put(m: Map<string, Map<number, number>>, n: number) {}", "put", 2)]
    [InlineData("Mixed.cs", "class C { void Put(Dictionary<string, (int, int)> m, (int, int) n, int[] a) {} }", "Put", 3)]
    public void GenericParameterTypes_CountOnlyParameterSeparators(string path, string source, string name, int parameters)
    {
        Assert.Equal(parameters, Function(Analyze(path, source), name).ParameterCount);
    }

    [Theory]
    [InlineData("Defaults.cs", "class C { void Put(bool a = 1 < 2, bool b = 3 > 2, int c = 0) {} }")]
    [InlineData("defaults.cpp", "void Put(bool a = 1 < 2, bool b = 3 > 2, int c = 0) {}")]
    [InlineData("defaults.ts", "function Put(a = lo < hi, b = hi > lo, c = [1, 2]) {}")]
    [InlineData("defaults.js", "function Put(a = lo < hi, b = hi > lo, c = [1, 2]) {}")]
    [InlineData("defaults.py", "def Put(a = lo < hi, b = hi > lo, c = [1, 2]):\n    pass")]
    [InlineData("defaults.rb", "def Put(a = lo < hi, b = hi > lo, c = [1, 2])\nend")]
    public void ParameterDefaults_ComparisonsDoNotHideSeparators(string path, string source)
    {
        Assert.Equal(3, Function(Analyze(path, source), "Put").ParameterCount);
    }

    [Theory]
    [InlineData("defaults.cpp", "void Put(Map<int, int> m = make<int, int>(), bool a = 1 < 2, bool b = 3 > 2) {}")]
    [InlineData("defaults.ts", "function Put(m = make<string, number>(), a = lo < hi, b = hi > lo) {}")]
    public void ParameterDefaults_GenericCallsDoNotAddParameters(string path, string source)
    {
        Assert.Equal(3, Function(Analyze(path, source), "Put").ParameterCount);
    }

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
