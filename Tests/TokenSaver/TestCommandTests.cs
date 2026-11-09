using TokenSaver.Shape;
using Xunit;

namespace Tests.TokenSaver;

public class TestCommandTests
{
    [Theory]
    [InlineData("node --test Tests/wwwroot/js/*.test.mjs")]
    [InlineData("node.exe --test-name-pattern=widget --test a.test.js")]
    [InlineData("node --test --test-reporter=spec --test-name-pattern=\"one (or two)\" a.test.js")]
    [InlineData("cd /c/source/project && dotnet test Tests/Tests.csproj --no-build --filter \"FullyQualifiedName~X\" 2>&1 | tail -20")]
    [InlineData("cd /work && timeout 900 dotnet test -nologo -v q 2>&1 | grep -E \"Passed!|Failed|error\" | tail -30")]
    [InlineData("dotnet test --logger \"console;verbosity=detailed\" 2>&1 | tail -40")]
    [InlineData("cd /work && node --test Tests/wwwroot/js/*.test.mjs 2>&1 | tail -15")]
    [InlineData("timeout 300 node --test --test-name-pattern=widget Tests/a.test.mjs 2>&1")]
    [InlineData("Tests/bin/Debug/net10.0/Tests.exe -class A -class B 2>&1 | tail -30")]
    [InlineData("Set-Location C:\\source\\project; dotnet test Tests/Tests.csproj --no-build 2>&1 | Select-Object -Last 30")]
    [InlineData("$env:X='1'; dotnet test Tests/Tests.csproj *>&1 | Select-Object -First 30")]
    [InlineData("$env:X = 'literal value'; dotnet test")]
    [InlineData("& .\\tests\\X.Tests\\bin\\Debug\\net10.0\\X.Tests.exe -class Example")]
    [InlineData("& 'C:\\my project\\X.Tests.exe' -class Example")]
    [InlineData("./X.Tests -class Example")]
    [InlineData("export X=1 Y=two; X=3 timeout 20s pytest -v | head -n 50; echo \"exit=$?\"")]
    [InlineData("pushd '/my project'; pytest -v | grep 'PASSED' | head; Write-Output done")]
    [InlineData("Set-Location -LiteralPath 'C:\\my project'; dotnet test")]
    [InlineData(".venv\\Scripts\\pytest.exe -v")]
    [InlineData(".venv/bin/pytest -v")]
    [InlineData("./node_modules/.bin/jest --ci")]
    [InlineData("./node_modules/.bin/vitest run")]
    [InlineData("./node_modules/.bin/mocha")]
    [InlineData("./node_modules/.bin/playwright test")]
    [InlineData("npm --prefix UITests run test:e2e")]
    [InlineData("npm --prefix=UITests run test:e2e")]
    [InlineData("pnpm -C web test")]
    [InlineData("npm run test:node")]
    [InlineData("npx playwright test --config playwright.local.config.js --grep 'board|card'")]
    [InlineData("python -m unittest -v")]
    [InlineData("python3 -m pytest -v")]
    [InlineData("poetry run pytest -v")]
    [InlineData("pipenv run python -m pytest -v")]
    [InlineData("hatch test")]
    [InlineData("cargo nextest run")]
    [InlineData("gotestsum -- ./...")]
    [InlineData("deno test")]
    [InlineData("dotnet run --project IntegrationTest")]
    [InlineData("dotnet run --project ./IntegrationTest/IntegrationTest.csproj --no-build")]
    [InlineData("pytest -v | Select-String -Pattern PASSED")]
    [InlineData("pytest -v | findstr /I PASSED")]
    [InlineData("node --test 2>&1 | grep -B2 -A12 \"^not ok\\|✖\" | head -40")]
    [InlineData("dotnet test --logger \"console;verbosity=normal\" 2>&1 | grep -B2 -A 8 \"Error Message\" | head -60")]
    public void RecognizesOnlySupportedTestForms(string command) =>
        Assert.Equal(CommandShape.TestRun, CommandShapes.Classify(command));

    [Theory]
    [InlineData("node script.js --test")]
    [InlineData("node --test-name-pattern=--test script.js")]
    [InlineData("node --import --test script.js")]
    [InlineData("node -- --test")]
    [InlineData("node --eval 'console.log(1)' --test")]
    [InlineData("node --test -e 'console.log(1)'")]
    [InlineData("dotnet test | sort")]
    [InlineData("dotnet test > file")]
    [InlineData("dotnet test 2> file")]
    [InlineData("dotnet test 2>&1more")]
    [InlineData("dotnet test $(echo more)")]
    [InlineData("dotnet test --filter $FILTER")]
    [InlineData("dotnet test --filter '$FILTER'")]
    [InlineData("dotnet test && git status")]
    [InlineData("dotnet test || echo failed")]
    [InlineData("dotnet test; node --test")]
    [InlineData("dotnet build && dotnet test")]
    [InlineData("dotnet build 2>&1 | tail -20 && echo tests && dotnet test 2>&1 | tail -12")]
    [InlineData("dotnet test &")]
    [InlineData("dotnet test | tail -20 secret.txt")]
    [InlineData("dotnet test; tail -20")]
    [InlineData("dotnet test | grep PASS secret.txt")]
    [InlineData("dotnet test | grep -f secret.txt")]
    [InlineData("dotnet test | Select-String -Path secret.txt -Pattern PASS")]
    [InlineData("dotnet test | findstr /F:secret.txt PASS")]
    [InlineData("dotnet test; echo $(cat secret.txt)")]
    [InlineData("dotnet test; echo $SECRET")]
    [InlineData("dotnet test; echo done | grep PASS")]
    [InlineData("$env:X=$SECRET; dotnet test")]
    [InlineData("X=$SECRET dotnet test")]
    [InlineData("cd $(pwd); dotnet test")]
    [InlineData("cd `pwd`; dotnet test")]
    [InlineData("dotnet test --filter 'unterminated")]
    [InlineData("dotnet te''st")]
    [InlineData("dotnet \"te\"\"st\"")]
    [InlineData("dotnet test\ngit status")]
    [InlineData("dotnet test \\; cat private.txt")]
    [InlineData("dotnet run --project MyApp")]
    [InlineData("dotnet run --project IntegrationTest --project Other")]
    [InlineData("docker run image pytest")]
    [InlineData("git status -s | head")]
    [InlineData("rg -n 'a|b' src/")]
    [InlineData("cd /work && find . -name '*.cs'")]
    public void UnsupportedOrAmbiguousCommandsDecline(string command) =>
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));

    [Theory]
    [InlineData("✔ first test (1.3ms)\n✔ second test (2.1ms)", "✖ failing test (1ms)\nℹ tests 3\nℹ pass 2\nℹ fail 1")]
    [InlineData("ok 1 - first test\nok 2 - second test", "not ok 3 - broken\nok 4 - skipped # SKIP reason\nok 5 - todo # TODO reason\n1..5")]
    [InlineData("test_one (pkg.mod.Test.test_one) ... ok\ntest_two (pkg.mod.Test.test_two) ... ok", "test_three (pkg.mod.Test.test_three) ... FAIL\nRan 3 tests\nFAILED (failures=1)")]
    [InlineData("PASS [ 0.012s] crate::module::one\nPASS [ 0.345s] crate::module::two", "FAIL [ 0.123s] crate::module::three\nSummary [0.5s] 3 tests run: 2 passed, 1 failed")]
    [InlineData("ok  \texample.com/one\t0.123s\nok  \texample.com/two\t0.234s", "FAIL\texample.com/three\t0.1s\n?\texample.com/four\t[no test files]")]
    [InlineData("first deno test ... ok (3ms)\nsecond deno test ... ok (1ms)", "third deno test ... FAILED (1ms)\nFAILED | 2 passed | 1 failed")]
    public void ElidesPassResultsPreservesFailureDetailsAndIsStable(string passes, string remainder)
    {
        var input = passes + "\n" + remainder + "\n";
        var result = ShapeFilters.Apply(input, CommandShape.TestRun);
        Assert.Equal("[... 2 passed ...]\n" + remainder + "\n", result);
        Assert.True(result.Length < input.Length);
        Assert.Equal(result, ShapeFilters.Apply(result, CommandShape.TestRun));
    }

    [Theory]
    [InlineData("ok 1 - skipped # SKIP reason\nok 2 - todo # TODO pending\n")]
    [InlineData("PASS [not a duration] test\nPassed! 3 passed\n")]
    [InlineData("Tests.Example.Test [STARTING]\nTests.Example.Test [FINISHED] Time: 0.009s\n")]
    public void NonPassingOrUnprovenLinesStayVerbatim(string input) =>
        Assert.Equal(input, ShapeFilters.Apply(input, CommandShape.TestRun));
}
