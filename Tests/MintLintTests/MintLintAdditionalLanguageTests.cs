using MintLint;
using Xunit;

namespace Tests.MintLintTests;

public sealed class MintLintAdditionalLanguageTests
{
    [Fact]
    public void Php_ExtractsFunctionsClassesImportsAndComplexity()
    {
        const string source = """
            <main>HTML outside PHP is ignored.</main>
            <?php
            use App\Services\Clock;

            class Greeter
            {
                private string $prefix;

                public function greet(string $name, bool $loud)
                {
                    if ($loud && strlen($name) > 0) {
                        return $this->prefix . strtoupper($name);
                    }

                    return $this->prefix . $name;
                }
            }

            function load_template(string $path)
            {
                return file_get_contents($path);
            }
            ?>
            <footer>More HTML.</footer>
            """;

        FileMetrics metrics = Analyze("sample.php", source);

        Assert.Equal(3, Function(metrics, "greet").Cyclomatic);
        Assert.Equal(2, Function(metrics, "greet").ParameterCount);
        Assert.Equal(1, Function(metrics, "load_template").ParameterCount);
        Assert.Equal(1, metrics.FanOut);
        Assert.Equal(1, metrics.AmbientDependencies);
        ClassMetrics type = Assert.Single(metrics.Classes);
        Assert.Equal("Greeter", type.Name);
        Assert.Equal(1, type.MethodCount);
        Assert.Equal(1, type.FieldCount);
    }

    [Fact]
    public void Ruby_ExtractsMethodsFieldsImportsAndKeywordScopes()
    {
        const string source = """
            require "json"

            class Greeter
              def initialize(prefix)
                @prefix = prefix
              end

              def greet name, loud
                if loud && !name.empty?
                  @prefix + name.upcase
                elsif name.empty?
                  @prefix
                else
                  @prefix + name
                end
              end
            end
            """;

        FileMetrics metrics = Analyze("sample.rb", source);

        Assert.Equal(4, Function(metrics, "greet").Cyclomatic);
        Assert.Equal(2, Function(metrics, "greet").ParameterCount);
        Assert.Equal(1, metrics.FanOut);
        ClassMetrics type = Assert.Single(metrics.Classes);
        Assert.Equal("Greeter", type.Name);
        Assert.Equal(2, type.MethodCount);
        Assert.Equal(1, type.FieldCount);
        Assert.Equal(1, type.Lcom4);
    }

    [Fact]
    public void Bash_ExtractsFunctionsSourcesAndShellKeywordScopes()
    {
        const string source = """
            #!/usr/bin/env bash
            source "./lib.sh"

            greet() {
              local name="$1"
              if [[ -n "$name" && "$LOUD" == "1" ]]; then
                echo "HELLO $name"
              elif [[ -z "$name" ]]; then
                echo "missing"
              else
                echo "Hello $name"
              fi
            }
            """;

        FileMetrics metrics = Analyze("sample.sh", source);

        Assert.Equal(4, Function(metrics, "greet").Cyclomatic);
        Assert.Equal(0, Function(metrics, "greet").ParameterCount);
        Assert.Equal(1, metrics.FanOut);
        Assert.Empty(metrics.Classes);
    }

    [Fact]
    public void PowerShell_ExtractsFunctionsMethodsModulesAndCaseInsensitiveKeywords()
    {
        const string source = """
            using module "./Tools.psm1"

            CLASS Greeter {
                [string] $Prefix

                [string] Greet([string]$Name, [bool]$Loud) {
                    IF ($Loud -and $Name) {
                        return $Name.ToUpperInvariant()
                    }
                    ELSEIF (!$Name) {
                        return $this.Prefix
                    }
                    return $this.Prefix + $Name
                }
            }

            FUNCTION Invoke-Greeting {
                [CmdletBinding()]
                param([string]$Name)
                if ($Name) { return $Name }
                return "missing"
            }
            """;

        FileMetrics metrics = Analyze("sample.ps1", source);

        Assert.Equal(4, Function(metrics, "Greet").Cyclomatic);
        Assert.Equal(2, Function(metrics, "Greet").ParameterCount);
        Assert.Equal(2, Function(metrics, "Invoke-Greeting").Cyclomatic);
        Assert.Equal(1, Function(metrics, "Invoke-Greeting").ParameterCount);
        Assert.Equal(1, metrics.FanOut);
        ClassMetrics type = Assert.Single(metrics.Classes);
        Assert.Equal("Greeter", type.Name);
        Assert.Equal(1, type.MethodCount);
        Assert.Equal(1, type.FieldCount);
    }

    [Fact]
    public void Ruby_HandlesSemicolonSeparatedOneLineMethods()
    {
        FileMetrics metrics = Analyze("compact.rb", "def choose(value); if value; 1; else; 0; end; end");

        Assert.Equal(2, Function(metrics, "choose").Cyclomatic);
        Assert.Equal(1, Function(metrics, "choose").ParameterCount);
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
