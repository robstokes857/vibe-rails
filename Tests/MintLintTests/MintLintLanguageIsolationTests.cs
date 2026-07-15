using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// Pins the boundary between languages.
///
/// One metric engine scores every language, so a keyword added for a new language will
/// silently rescore every existing one unless the lookup is gated on the source language.
/// These tests fail if that gating regresses: each one feeds a language a word that is
/// reserved somewhere else but an ordinary identifier here.
/// </summary>
public sealed class MintLintLanguageIsolationTests
{
    [Fact]
    public void CSharp_CatchFilter_CountsTheCatchOnce()
    {
        // `when` is a Ruby case arm, but in C# it is part of the catch it filters. Counting
        // it as its own decision would score this method 3 instead of 2.
        const string source = """
            using System;

            public static class Loader
            {
                public static string Read(string path)
                {
                    try
                    {
                        return path;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException)
                    {
                        return string.Empty;
                    }
                }
            }
            """;

        Assert.Equal(2, Function(Analyze("Loader.cs", source), "Read").Cyclomatic);
    }

    [Fact]
    public void CSharp_SwitchArmGuard_CountsOnlyTheArms()
    {
        const string source = """
            public static class Router
            {
                public static int Route(int value, bool flag)
                {
                    switch (value)
                    {
                        case 1 when flag:
                            return 10;
                        case 2:
                            return 20;
                        default:
                            return 0;
                    }
                }
            }
            """;

        // Two `case` arms; the `when` guard qualifies the first rather than adding a third.
        Assert.Equal(3, Function(Analyze("Router.cs", source), "Route").Cyclomatic);
    }

    [Theory]
    [InlineData("until")]
    [InlineData("unless")]
    [InlineData("rescue")]
    [InlineData("when")]
    public void CSharp_LocalNamedAfterAnotherLanguagesKeyword_IsNotADecision(string name)
    {
        // Each of these is reserved in Ruby and an ordinary C# identifier. A straight
        // token-text match would score every read of the variable as a branch.
        string source = $$"""
            public static class Sample
            {
                public static int Run()
                {
                    int {{name}} = 1;
                    return {{name}} + {{name}};
                }
            }
            """;

        Assert.Equal(1, Function(Analyze("Sample.cs", source), "Run").Cyclomatic);
    }

    [Fact]
    public void JavaScript_PromiseChainAndCommonJs_AreOperandsNotKeywords()
    {
        // `then`, `require`, and `module` are Bash/Ruby/PHP keywords. Treating them as
        // operators here would shift Halstead volume for essentially every CommonJS file.
        const string source = """
            const fs = require('fs');

            function load(path) {
                return fs.read(path).then(text => text.trim());
            }

            module.exports = { load };
            """;

        FileMetrics withKeywords = Analyze("loader.js", source);
        FileMetrics withPlainNames = Analyze(
            "loader.js",
            source.Replace("then", "map").Replace("require", "load1").Replace("module", "exportsHost"));

        // Renaming the identifiers must not move the score: they were never keywords here.
        Assert.Equal(withPlainNames.HalsteadVolume, withKeywords.HalsteadVolume);
    }

    [Fact]
    public void Java_IdentifierNamedWhen_IsNotADecision()
    {
        const string source = """
            public class Scheduler {
                int run(int when) {
                    return when + when;
                }
            }
            """;

        Assert.Equal(1, Function(Analyze("Scheduler.java", source), "run").Cyclomatic);
    }

    [Fact]
    public void Ruby_CaseArms_EachCountAsADecision()
    {
        const string source = """
            def classify(value)
              case value
              when 1 then :one
              when 2 then :two
              else :other
              end
            end
            """;

        // Base 1 plus one per `when`; the `case` header itself is not a decision.
        Assert.Equal(3, Function(Analyze("classify.rb", source), "classify").Cyclomatic);
    }

    [Fact]
    public void Ruby_UnlessAndUntil_AreDecisions()
    {
        const string source = """
            def drain(queue)
              unless queue.empty?
                until queue.empty?
                  queue.pop
                end
              end
            end
            """;

        Assert.Equal(3, Function(Analyze("drain.rb", source), "drain").Cyclomatic);
    }

    [Fact]
    public void PowerShell_LogicalOperators_AreDecisions()
    {
        const string source = """
            function Test-Value {
                param([int]$Value)
                if ($Value -gt 0 -and $Value -lt 10) {
                    return $true
                }
                return $false
            }
            """;

        // Base 1 + `if` + `-and`.
        Assert.Equal(3, Function(Analyze("Test-Value.ps1", source), "Test-Value").Cyclomatic);
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
