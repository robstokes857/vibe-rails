using PyBridge;
using PyBridge.Tests;

// =====================================================================================
//  PyBridge — self-checking test suite
//
//  Exercises the public API against real Python processes:
//    TEST A — a script that needs a library missing from its conda env (ModuleNotFoundError)
//    TEST B — buggy code that throws an unhandled runtime exception (ZeroDivisionError)
//    TEST C — StreamCodeAsync: live stdout/stderr lines with realtime pacing
//    TEST D — StartInteractive: two-way stdin/stdout conversation (echo-upper loop)
//    TEST E — PythonSession: request/response with python/worker.py (raw + typed JSON)
//    TEST F — timeout enforcement and explicit KillAsync
//    TEST G — StreamAsync surfaces failure as PythonExecutionException after the last line
//    TEST H — ProbeAsync reports the real interpreter
//    TEST I — PythonLocator.Discover finds at least one interpreter
//    TEST J — EmbeddedPython runtime (opt-in via PYBRIDGE_TEST_EMBEDDED=1)
//
//  Conda-dependent tests (A, B) SKIP — not fail — on machines without those envs.
//
//  Run with:  dotnet run --project src/PyBridge.Tests
//  Exit code: 0 if every non-skipped check passed, 1 otherwise.
// =====================================================================================

var failures = 0;

void Check(string name, bool condition, string? detail = null)
{
    Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}{(string.IsNullOrEmpty(detail) ? "" : $" — {detail}")}");
    if (!condition)
        failures++;
}

void Header(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine("  " + title);
    Console.WriteLine(new string('=', 78));
}

void Skip(string reason) => Console.WriteLine($"  [SKIP] {reason}");

// Every test runs inside a guard so one unexpected exception fails that test, not the suite.
async Task GuardAsync(string testName, Func<Task> test)
{
    try
    {
        await test();
    }
    catch (Exception ex)
    {
        Check($"{testName} ran to completion without an unexpected exception", false,
            $"{ex.GetType().Name}: {ex.Message}");
    }
}

string missingLibScript = LocateRepoFile(Path.Combine("python", "tests", "fail_missing_lib.py"));
string runtimeScript = LocateRepoFile(Path.Combine("python", "tests", "fail_runtime.py"));
string workerScript = LocateRepoFile(Path.Combine("python", "worker.py"));

Header("PyBridge test suite");
Console.WriteLine($"missing-lib script : {missingLibScript}");
Console.WriteLine($"runtime-err script : {runtimeScript}");
Console.WriteLine($"worker script      : {workerScript}");

// Conda envs are optional on other machines: resolve them up-front, skip A/B when absent.
var bareOptions = TryCondaOptions("barebones", out var bareReason);
var pybridgeOptions = TryCondaOptions("pybridge", out var pybridgeReason);

// -------------------------------------------------------------------------------------
// TEST A — missing library in a conda env. The SAME script fails in a bare env and
//          succeeds in one that has numpy: the wrapper must faithfully report each outcome.
// -------------------------------------------------------------------------------------
Header("TEST A — missing library in a conda env (ModuleNotFoundError)");

if (bareOptions is null)
{
    Skip($"conda env 'barebones' is unavailable — {bareReason}");
}
else
{
    await GuardAsync("TEST A", async () =>
    {
        var bareRunner = new PythonRunner(bareOptions);
        var a = await bareRunner.RunFileAsync(missingLibScript);

        Console.WriteLine($"  (env 'barebones') exit code = {a.ExitCode}, IsSuccess = {a.IsSuccess}");
        Console.WriteLine("  captured stderr (last line):");
        Console.WriteLine($"    | {LastNonEmptyLine(a.StandardError)}");

        Check("wrapper returned a result (did not throw)", true);
        Check("IsSuccess is false", !a.IsSuccess);
        Check("exit code is non-zero", a.ExitCode != 0, $"exit={a.ExitCode}");
        Check("stderr captured ModuleNotFoundError", a.StandardError.Contains("ModuleNotFoundError"));
        Check("stderr names the missing module (numpy)", a.StandardError.Contains("numpy"));

        var ensureThrew = false;
        try
        {
            a.EnsureSuccess();
        }
        catch (PythonExecutionException ex)
        {
            ensureThrew = true;
            Check("thrown exception carries the exit code", ex.ExitCode == a.ExitCode, $"ExitCode={ex.ExitCode}");
            Check("thrown exception carries the stderr", ex.StandardError.Contains("numpy"));
        }
        Check("EnsureSuccess() threw PythonExecutionException", ensureThrew);

        // Contrast: same script, an env that HAS numpy -> success. Proves it's the env, not the wrapper.
        if (pybridgeOptions is null)
        {
            Skip($"contrast run skipped — conda env 'pybridge' is unavailable — {pybridgeReason}");
        }
        else
        {
            var okRunner = new PythonRunner(pybridgeOptions);
            var aOk = await okRunner.RunFileAsync(missingLibScript);
            Console.WriteLine($"  (env 'pybridge') exit code = {aOk.ExitCode}, IsSuccess = {aOk.IsSuccess}");
            Check("same script SUCCEEDS in an env that has numpy", aOk.IsSuccess);
            Check("stdout shows the script actually ran", aOk.StandardOutput.Contains("numpy is present"));
        }
    });
}

// -------------------------------------------------------------------------------------
// TEST B — buggy code, unhandled runtime exception. Partial stdout AND the traceback
//          must both be captured, and the .NET host must not crash.
// -------------------------------------------------------------------------------------
Header("TEST B — unhandled runtime exception (ZeroDivisionError)");

if (pybridgeOptions is null)
{
    Skip($"conda env 'pybridge' is unavailable — {pybridgeReason}");
}
else
{
    await GuardAsync("TEST B", async () =>
    {
        var condaRunner = new PythonRunner(pybridgeOptions);
        var b = await condaRunner.RunFileAsync(runtimeScript);

        Console.WriteLine($"  exit code = {b.ExitCode}, IsSuccess = {b.IsSuccess}");
        Console.WriteLine("  captured stdout (before the crash):");
        foreach (var line in b.StandardOutput.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            Console.WriteLine($"    | {line}");
        Console.WriteLine("  captured stderr (last line):");
        Console.WriteLine($"    | {LastNonEmptyLine(b.StandardError)}");

        Check("wrapper returned a result (did not throw)", true);
        Check("IsSuccess is false", !b.IsSuccess);
        Check("exit code is 1", b.ExitCode == 1, $"exit={b.ExitCode}");
        Check("partial stdout before the crash was captured", b.StandardOutput.Contains("starting computation"));
        Check("stderr contains a Python traceback", b.StandardError.Contains("Traceback"));
        Check("stderr names ZeroDivisionError", b.StandardError.Contains("ZeroDivisionError"));
    });
}

// -------------------------------------------------------------------------------------
// The remaining tests run on whatever interpreter Discover() finds — fully portable.
// -------------------------------------------------------------------------------------
Header("Interpreter discovery for the portable tests");

var options = PythonRunnerOptions.Discover();
var runner = new PythonRunner(options);
Console.WriteLine($"  discovered interpreter: {options.PythonExecutable}");

// -------------------------------------------------------------------------------------
// TEST C — StreamCodeAsync yields stdout AND stderr lines live, in order, with
//          non-decreasing Elapsed timestamps that prove realtime (not end-of-run) delivery.
// -------------------------------------------------------------------------------------
Header("TEST C — StreamCodeAsync: live lines, sources, ordering, pacing");

await GuardAsync("TEST C", async () =>
{
    var pacedCode =
        "import sys, time\n" +
        "print('out 1', flush=True)\n" +
        "time.sleep(0.15)\n" +
        "print('err 1', file=sys.stderr, flush=True)\n" +
        "time.sleep(0.15)\n" +
        "print('out 2', flush=True)\n" +
        "time.sleep(0.15)\n" +
        "print('out 3', flush=True)\n";

    var streamed = new List<PythonOutputLine>();
    await foreach (var line in runner.StreamCodeAsync(pacedCode))
        streamed.Add(line);

    foreach (var line in streamed)
        Console.WriteLine($"    [{line.Elapsed.TotalSeconds,5:F2}s] {(line.IsError ? "stderr" : "stdout")} | {line.Text}");

    var stdoutTexts = streamed.Where(l => !l.IsError).Select(l => l.Text).ToList();
    var stderrTexts = streamed.Where(l => l.IsError).Select(l => l.Text).ToList();

    var elapsedNonDecreasing = true;
    for (var i = 1; i < streamed.Count; i++)
        if (streamed[i].Elapsed < streamed[i - 1].Elapsed)
            elapsedNonDecreasing = false;

    Check("all 4 lines arrived", streamed.Count == 4, $"count={streamed.Count}");
    Check("exactly one line came from stderr", stderrTexts.Count == 1 && stderrTexts[0] == "err 1");
    Check("stdout lines arrived in order", stdoutTexts.SequenceEqual(new[] { "out 1", "out 2", "out 3" }));
    Check("Elapsed timestamps are non-decreasing", elapsedNonDecreasing);
    Check("last stdout line arrived after >= 0.2s (realtime pacing)",
        streamed.Count > 0 && streamed[^1].Elapsed >= TimeSpan.FromSeconds(0.2),
        $"last={streamed[^1].Elapsed.TotalSeconds:F2}s");
});

// -------------------------------------------------------------------------------------
// TEST D — StartInteractive: write lines into a live process and read its replies back,
//          then prove non-interactive runs reject live input.
// -------------------------------------------------------------------------------------
Header("TEST D — StartInteractive: two-way conversation (echo-upper loop)");

await GuardAsync("TEST D", async () =>
{
    var echoUpperLoop =
        "import sys\n" +
        "for line in iter(sys.stdin.readline, ''):\n" +
        "    print(line.strip().upper(), flush=True)\n";

    await using (var run = runner.StartInteractive(["-c", echoUpperLoop]))
    {
        Check("interactive run reports SupportsLiveInput == true", run.SupportsLiveInput);

        var replies = new List<string>();
        foreach (var word in new[] { "alpha", "bravo", "charlie" })
            await run.WriteInputLineAsync(word);

        while (replies.Count < 3)
        {
            var line = await run.ReadOutputLineAsync();
            if (line is null)
                break;
            if (line.Value.IsError)
                continue; // stderr noise never counts as a reply
            replies.Add(line.Value.Text);
        }

        run.CompleteInput();
        var result = await run.Completion;

        Check("three replies arrived", replies.Count == 3, string.Join(", ", replies));
        Check("replies are upper-cased echoes in order",
            replies.SequenceEqual(new[] { "ALPHA", "BRAVO", "CHARLIE" }));
        Check("exit code 0 after EOF", result.ExitCode == 0, $"exit={result.ExitCode}");
        Check("IsSuccess is true", result.IsSuccess);
    }

    await using (var plain = runner.Start(["-c", "print('plain run')"]))
    {
        Check("non-interactive run reports SupportsLiveInput == false", !plain.SupportsLiveInput);

        var threw = false;
        try
        {
            await plain.WriteInputLineAsync("nope");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check("WriteInputLineAsync on a non-interactive run throws InvalidOperationException", threw);

        await plain.Completion;
    }
});

// -------------------------------------------------------------------------------------
// TEST E — PythonSession: one line in, one line out, stderr routed separately;
//          raw and typed (source-generated JSON) request/response; clean exit on EOF.
// -------------------------------------------------------------------------------------
Header("TEST E — PythonSession request/response with python/worker.py");

await GuardAsync("TEST E", async () =>
{
    var stderrLines = new List<string>();
    var session = runner.StartSession(workerScript);
    try
    {
        session.StandardErrorLine += stderrLines.Add;

        var readyLine = await session.WaitForReadyAsync("ready");
        Check("WaitForReadyAsync matched the 'ready' marker", readyLine == "ready");

        var pingReply = await session.SendAsync("""{"op": "ping"}""");
        Check("raw ping reply contains 'pong'", pingReply.Contains("pong"), pingReply);

        var addReply = await session.SendJsonAsync(
            new WorkerRequest { Op = "add", A = 2, B = 40 },
            WorkerJsonContext.Default.WorkerRequest,
            WorkerJsonContext.Default.WorkerReply);
        Check("typed add(2, 40) replied ok", addReply.Ok, addReply.Error);
        Check("typed add(2, 40) == 42", addReply.Ok && addReply.Result.GetInt32() == 42);

        var upperReply = await session.SendJsonAsync(
            new WorkerRequest { Op = "upper", Text = "knife" },
            WorkerJsonContext.Default.WorkerRequest,
            WorkerJsonContext.Default.WorkerReply);
        Check("typed upper('knife') == 'KNIFE'", upperReply.Ok && upperReply.Result.GetString() == "KNIFE");

        var unknownReply = await session.SendJsonAsync(
            new WorkerRequest { Op = "does-not-exist" },
            WorkerJsonContext.Default.WorkerRequest,
            WorkerJsonContext.Default.WorkerReply);
        Check("unknown op replied ok == false", !unknownReply.Ok);
        Check("unknown op carries a non-empty error", !string.IsNullOrEmpty(unknownReply.Error), unknownReply.Error);

        Check("at least one stderr line was captured via StandardErrorLine",
            stderrLines.Count > 0, stderrLines.Count > 0 ? stderrLines[0] : "(none)");
    }
    finally
    {
        await session.DisposeAsync(); // sends EOF; a well-behaved worker exits 0
    }

    var final = await session.Completion;
    Check("worker exited cleanly on EOF (exit code 0)", final.ExitCode == 0 && final.IsSuccess,
        $"exit={final.ExitCode}, killed={final.WasKilled}");
    Check("worker stderr mentions exiting on EOF", final.StandardError.Contains("worker exiting on EOF"));
});

// -------------------------------------------------------------------------------------
// TEST F — a runner-level timeout kills a runaway script, and KillAsync stops one on demand.
// -------------------------------------------------------------------------------------
Header("TEST F — timeout enforcement and explicit KillAsync");

await GuardAsync("TEST F", async () =>
{
    var timeoutRunner = new PythonRunner(CloneInterpreter(options, TimeSpan.FromSeconds(1)));
    var timedOut = await timeoutRunner.RunCodeAsync("import time; time.sleep(30)");
    Console.WriteLine($"  timeout run: RunTime = {timedOut.RunTime.TotalSeconds:F1}s, ExitCode = {timedOut.ExitCode}");
    Check("TimedOut is true", timedOut.TimedOut);
    Check("IsSuccess is false after a timeout", !timedOut.IsSuccess);

    await using (var run = runner.Start(["-c", "import time; time.sleep(30)"]))
    {
        await Task.Delay(300); // let the process actually start
        var killed = await run.KillAsync();
        Check("WasKilled is true", killed.WasKilled);
        Check("IsSuccess is false after a kill", !killed.IsSuccess);
    }
});

// -------------------------------------------------------------------------------------
// TEST G — stream mode has no result object, so failure must surface as an exception
//          thrown AFTER every line has been yielded.
// -------------------------------------------------------------------------------------
Header("TEST G — StreamCodeAsync throws PythonExecutionException after the last line");

await GuardAsync("TEST G", async () =>
{
    var yielded = new List<PythonOutputLine>();
    PythonExecutionException? streamException = null;
    try
    {
        await foreach (var line in runner.StreamCodeAsync("import sys; print('boom', file=sys.stderr); sys.exit(3)"))
            yielded.Add(line);
    }
    catch (PythonExecutionException ex)
    {
        streamException = ex;
    }

    Check("PythonExecutionException was thrown", streamException is not null);
    Check("exception carries exit code 3", streamException?.ExitCode == 3, $"ExitCode={streamException?.ExitCode}");
    Check("stderr line 'boom' was yielded before the throw", yielded.Any(l => l.IsError && l.Text == "boom"));
});

// -------------------------------------------------------------------------------------
// TEST H — ProbeAsync: the interpreter tells us what it actually is.
// -------------------------------------------------------------------------------------
Header("TEST H — ProbeAsync reports the real interpreter");

await GuardAsync("TEST H", async () =>
{
    var info = await runner.ProbeAsync();
    Console.WriteLine($"  probe: {info.Implementation} {info.Version} at {info.Executable}");
    Check("Version is non-empty", !string.IsNullOrEmpty(info.Version), info.Version);
    Check("Executable exists on disk", File.Exists(info.Executable), info.Executable);
});

// -------------------------------------------------------------------------------------
// TEST I — PythonLocator.Discover finds every interpreter on this machine, best first.
// -------------------------------------------------------------------------------------
Header("TEST I — PythonLocator.Discover");

await GuardAsync("TEST I", () =>
{
    var candidates = PythonLocator.Discover();
    foreach (var candidate in candidates)
        Console.WriteLine($"    {candidate.Kind,-20} {candidate.Executable}");
    Check("at least one interpreter was discovered", candidates.Count > 0, $"count={candidates.Count}");
    return Task.CompletedTask;
});

// -------------------------------------------------------------------------------------
// TEST J — EmbeddedPython: downloads ~11 MB from python.org on first use, so it is
//          opt-in via an environment variable rather than running on every invocation.
// -------------------------------------------------------------------------------------
Header("TEST J — EmbeddedPython runtime (opt-in)");

if (Environment.GetEnvironmentVariable("PYBRIDGE_TEST_EMBEDDED") == "1")
{
    await GuardAsync("TEST J", async () =>
    {
        var embeddedOptions = await EmbeddedPython.EnsureAsync();
        Console.WriteLine($"  embedded interpreter: {embeddedOptions.PythonExecutable}");
        var embeddedRunner = new PythonRunner(embeddedOptions);
        var result = await embeddedRunner.RunCodeAsync("print('embedded ok')");
        Check("embedded runtime ran the snippet", result.IsSuccess, $"exit={result.ExitCode}");
        Check("embedded runtime printed the marker", result.StandardOutputTrimmed == "embedded ok",
            result.StandardOutputTrimmed);
    });
}
else
{
    Skip("set PYBRIDGE_TEST_EMBEDDED=1 to download (~11 MB, first run only) and test the embedded runtime");
}

return Finish();


// ------------------------------------ helpers ----------------------------------------

int Finish()
{
    Header("Summary");
    if (failures == 0)
        Console.WriteLine("ALL CHECKS PASSED.");
    else
        Console.WriteLine($"{failures} CHECK(S) FAILED.");
    return failures == 0 ? 0 : 1;
}

// Resolves a conda env into runner options, or null (with a reason) when conda or the
// env is missing on this machine — so conda-dependent tests can SKIP instead of FAIL.
static PythonRunnerOptions? TryCondaOptions(string environmentName, out string reason)
{
    try
    {
        var options = PythonRunnerOptions.ForConda(environmentName);
        if (!File.Exists(options.PythonExecutable))
        {
            reason = $"python.exe not found at {options.PythonExecutable}";
            return null;
        }

        reason = string.Empty;
        return options;
    }
    catch (PythonExecutionException ex)
    {
        reason = ex.Message;
        return null;
    }
}

// Copies the interpreter identity (executable + activation PATH + env vars) so a second
// runner with different Timeout/etc. still launches a fully-activated interpreter.
static PythonRunnerOptions CloneInterpreter(PythonRunnerOptions source, TimeSpan? timeout = null)
{
    var clone = new PythonRunnerOptions
    {
        PythonExecutable = source.PythonExecutable,
        Timeout = timeout,
    };

    foreach (var entry in source.AdditionalPathEntries)
        clone.AdditionalPathEntries.Add(entry);
    foreach (var kvp in source.EnvironmentVariables)
        clone.EnvironmentVariables[kvp.Key] = kvp.Value;

    return clone;
}

static string LastNonEmptyLine(string text)
{
    var lines = text.Replace("\r\n", "\n").Split('\n');
    for (var i = lines.Length - 1; i >= 0; i--)
        if (!string.IsNullOrWhiteSpace(lines[i]))
            return lines[i].Trim();
    return "(empty)";
}

static string LocateRepoFile(string relativePath)
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }
    }

    throw new FileNotFoundException(
        $"Could not find {relativePath} by walking up from the executable or working directory.");
}
