using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// Covers the language constructs whose scope or dependency handling is easy to get wrong:
/// Ruby definitions without an <c>end</c>, Bash case arms, and ambient state reached through
/// an indexer or a scoped variable rather than a member access.
/// </summary>
public sealed class MintLintLanguageStructureTests
{
    [Fact]
    public void Ruby_EndlessMethod_DoesNotSwallowTheEnclosingEnd()
    {
        // `def one = 1` has no `end` of its own. If it opens a scope anyway, the class's
        // `end` closes that scope instead of the class — so the class stays open to the end
        // of the file and swallows `standalone`, which is not a method of it at all.
        const string source = """
            class Config
              def one = 1
            end

            def standalone(value)
              value
            end
            """;

        FileMetrics metrics = Analyze("config.rb", source);

        ClassMetrics type = Assert.Single(metrics.Classes);
        Assert.Equal("Config", type.Name);
        Assert.Equal(1, type.MethodCount);
        Assert.Equal(0, Function(metrics, "one").ParameterCount);
        Assert.Equal(1, Function(metrics, "standalone").ParameterCount);
    }

    [Fact]
    public void Ruby_EndlessMethodWithParameters_CountsThem()
    {
        FileMetrics metrics = Analyze("math.rb", "def square(x) = x * x");

        Assert.Equal(1, Function(metrics, "square").ParameterCount);
    }

    [Fact]
    public void Ruby_SetterMethod_IsNotMistakenForAnEndlessMethod()
    {
        // `def name=(value)` glues the `=` to the method name and still needs its `end`.
        // Reading it as an endless method would leave the class scope unbalanced.
        const string source = """
            class Person
              def name=(value)
                @name = value
              end

              def greet
                @name
              end
            end
            """;

        FileMetrics metrics = Analyze("person.rb", source);

        Assert.Equal(1, Function(metrics, "name").ParameterCount);
        Assert.Equal(2, Assert.Single(metrics.Classes).MethodCount);
    }

    [Fact]
    public void Ruby_ParenlessDefaultValue_CountsCommasInsideItAsOneParameter()
    {
        FileMetrics metrics = Analyze("at.rb", """
            def at(key, fallback = [1, 2, 3])
              key
            end
            """);

        Assert.Equal(2, Function(metrics, "at").ParameterCount);
    }

    [Fact]
    public void Bash_CaseArms_EachCountAsADecision()
    {
        const string source = """
            classify() {
              case "$1" in
                start) echo "up" ;;
                stop) echo "down" ;;
                *) echo "other" ;;
              esac
            }
            """;

        // Base 1 plus one per arm; the `case` header itself is not a decision, matching how
        // Ruby's `case`/`when` and C#'s `switch`/`case` are counted.
        Assert.Equal(4, Function(Analyze("classify.sh", source), "classify").Cyclomatic);
    }

    [Fact]
    public void Php_Superglobal_ReadThroughIndexer_IsAnAmbientDependency()
    {
        const string source = """
            <?php
            function home_path()
            {
                return $_ENV["HOME"];
            }
            """;

        Assert.True(Analyze("env.php", source).AmbientDependencies > 0);
    }

    [Fact]
    public void Ruby_EnvReadThroughIndexer_IsAnAmbientDependency()
    {
        const string source = """
            def home_path
              ENV["HOME"]
            end
            """;

        Assert.True(Analyze("env.rb", source).AmbientDependencies > 0);
    }

    [Fact]
    public void PowerShell_EnvScopedVariable_IsAnAmbientDependency()
    {
        const string source = """
            function Get-HomePath {
                return $env:HOME
            }
            """;

        Assert.True(Analyze("Get-HomePath.ps1", source).AmbientDependencies > 0);
    }

    [Fact]
    public void PowerShell_OrdinaryScopedVariable_IsNotAnAmbientDependency()
    {
        // `$script:count` is a scope of user variables, not the environment.
        const string source = """
            function Get-Count {
                return $script:count
            }
            """;

        Assert.Equal(0, Analyze("Get-Count.ps1", source).AmbientDependencies);
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
