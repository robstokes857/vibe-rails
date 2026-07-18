using System.Diagnostics;
using PyBridge;
using PyBridge.Console;

// =====================================================================================
//  PyBridge — proof-of-concept console
//
//  Goal: demonstrate that the PyBridge library can call a real Python script and that
//  whatever the script does at a terminal happens here too, with stdin / stdout / stderr /
//  exit code all captured and handed back to the C# caller.
//
//  DEMO 1  — pipe stdin in, capture stdout/stderr/exit code
//  DEMO 2  — run 'classifier.py check' and parse its JSON (AOT-safe source-gen)
//  DEMO 3  — run the ResNet classifier (real payload; failures relayed faithfully)
//  DEMO 4  — live streaming callbacks, line-by-line as the process runs
//  DEMO 5  — non-zero exit codes and EnsureSuccess() exception handling
//  DEMO 6  — interpreter discovery (PythonLocator) and probing (ProbeAsync)
//  DEMO 7  — await foreach streaming with arrival timestamps (StreamCodeAsync)
//  DEMO 8  — two-way interactive: stream stdin in while reading stdout out
//  DEMO 9  — warm session against python/worker.py (StartSession / SendJsonAsync)
//  DEMO 10 — private embedded python.org runtime (EmbeddedPython.EnsureAsync)
//
//  Run me with:   dotnet run --project src/PyBridge.Console
// =====================================================================================

Header("PyBridge POC — calling Python from .NET (AOT-friendly, via CliWrap)");

// Locate python/classifier.py by walking up from the executable / working directory.
string scriptPath;
try
{
    scriptPath = LocateClassifierScript();
}
catch (FileNotFoundException ex)
{
    System.Console.Error.WriteLine(ex.Message);
    return 1;
}
System.Console.WriteLine($"Using script : {scriptPath}");

// A single runner, reused across calls. Prefer the CUDA-enabled `pybridge` conda env if it
// exists (so DEMO 3 uses the GPU); otherwise fall back to whatever `python` is on PATH.
// Override the env name with the PYBRIDGE_CONDA_ENV environment variable.
var runner = BuildRunner(out var interpreterNote);
System.Console.WriteLine($"Interpreter  : {runner.Options.PythonExecutable}");
System.Console.WriteLine($"             : {interpreterNote}");

// -------------------------------------------------------------------------------------
// DEMO 1 — stdin in, stdout/stderr out, exit code captured. Proves the core round-trip.
// -------------------------------------------------------------------------------------
Header("DEMO 1 — pipe stdin in, capture stdout/stderr/exit code");
{
    const string code =
        "import sys; " +
        "data = sys.stdin.read().strip(); " +
        "print('python received:', data); " +
        "print('shouting it back:', data.upper()); " +
        "sys.stderr.write('(this line came from stderr)\\n')";

    var result = await runner.RunCodeAsync(code, standardInput: "hello from C#");

    System.Console.WriteLine($"exit code    : {result.ExitCode}");
    System.Console.WriteLine($"ran for      : {result.RunTime.TotalMilliseconds:F0} ms");
    System.Console.WriteLine("captured stdout:");
    PrintIndented(result.StandardOutput);
    System.Console.WriteLine("captured stderr:");
    PrintIndented(result.StandardError);
}

// -------------------------------------------------------------------------------------
// DEMO 2 — run classifier.py's dependency-free 'check' command and parse its JSON output.
//          Uses the AOT-safe ReadJsonOutput<T>(JsonTypeInfo<T>) helper.
// -------------------------------------------------------------------------------------
Header("DEMO 2 — run 'classifier.py check' and parse the JSON it prints");
bool torchAvailable = false;
{
    var result = await runner.RunFileAsync(scriptPath, "check");
    System.Console.WriteLine($"exit code    : {result.ExitCode}");

    if (result.IsSuccess)
    {
        var report = result.ReadJsonOutput(PyJsonContext.Default.CheckReport);
        if (report is not null)
        {
            torchAvailable = report.TorchAvailable;
            System.Console.WriteLine($"  python     : {report.PythonVersion}");
            System.Console.WriteLine($"  executable : {report.PythonExecutable}");
            System.Console.WriteLine($"  platform   : {report.Platform}");
            System.Console.WriteLine($"  torch      : {(report.TorchAvailable ? report.TorchVersion : "not installed")}");
            System.Console.WriteLine($"  cuda       : {(report.TorchAvailable ? report.CudaAvailable.ToString() : "n/a")}");
            if (report.CudaAvailable)
                System.Console.WriteLine($"  gpu        : {report.GpuName}");
        }
    }
    else
    {
        System.Console.WriteLine("check failed:");
        PrintIndented(result.StandardError);
    }
}

// -------------------------------------------------------------------------------------
// DEMO 3 — the real payload: run the ResNet classifier.
//          If PyTorch is installed, we parse and print predictions. If not, we show that
//          the wrapper faithfully relays the SAME failure (stderr + exit code 3) you'd get
//          from the terminal — which is exactly the contract we promised.
// -------------------------------------------------------------------------------------
Header("DEMO 3 — run the ResNet classifier ('classifier.py classify')");
{
    var result = await runner.RunFileAsync(scriptPath, "classify", "--model", "resnet18", "--topk", "5");
    System.Console.WriteLine($"exit code    : {result.ExitCode}");

    if (result.IsSuccess)
    {
        var classify = result.ReadJsonOutput(PyJsonContext.Default.ClassifyResult);
        if (classify is not null)
        {
            System.Console.WriteLine($"  model      : {classify.Model} on {classify.Device} " +
                                     $"({(classify.SyntheticImage ? "synthetic image" : "supplied image")})");
            System.Console.WriteLine($"  inference  : {classify.InferenceSeconds:F3} s");
            System.Console.WriteLine("  top predictions:");
            foreach (var p in classify.Predictions ?? [])
                System.Console.WriteLine($"    #{p.Rank}  {p.Confidence,8:P2}  {p.Label}");
        }
    }
    else
    {
        System.Console.WriteLine("Classifier did not run to success — here's what it reported");
        System.Console.WriteLine("(exit code 3 == PyTorch not installed; install it per python/README.md):");
        PrintIndented(result.StandardError);
    }
}

// -------------------------------------------------------------------------------------
// DEMO 4 — live streaming: get stdout/stderr line-by-line as the process runs.
// -------------------------------------------------------------------------------------
Header("DEMO 4 — live streaming of output as it is produced");
{
    await runner.RunFileAsync(
        scriptPath,
        arguments: ["check"],
        onStandardOutputLine: line => System.Console.WriteLine($"  [live::stdout] {line}"),
        onStandardErrorLine: line => System.Console.WriteLine($"  [live::stderr] {line}"));
}

// -------------------------------------------------------------------------------------
// DEMO 5 — error handling: non-zero exit codes are captured, and EnsureSuccess() throws
//          a rich exception on demand.
// -------------------------------------------------------------------------------------
Header("DEMO 5 — non-zero exit codes and exception handling");
{
    var result = await runner.RunCodeAsync(
        "import sys; sys.stderr.write('deliberate failure\\n'); sys.exit(7)");

    System.Console.WriteLine($"captured exit code : {result.ExitCode}  (IsSuccess={result.IsSuccess})");

    try
    {
        result.EnsureSuccess();
    }
    catch (PythonExecutionException ex)
    {
        System.Console.WriteLine($"EnsureSuccess() threw as expected: PythonExecutionException (ExitCode={ex.ExitCode})");
        System.Console.WriteLine($"  captured stderr: {ex.StandardError.Trim()}");
    }
}

// -------------------------------------------------------------------------------------
// DEMO 6 — discovery & probe: what interpreters live on this machine, and what is the
//          runner actually talking to? Discover() is filesystem-only; ProbeAsync() asks
//          the configured interpreter to describe itself.
// -------------------------------------------------------------------------------------
Header("DEMO 6 — interpreter discovery (PythonLocator) and probing (ProbeAsync)");
{
    var candidates = PythonLocator.Discover();
    System.Console.WriteLine($"found {candidates.Count} interpreter(s), best first:");
    foreach (var candidate in candidates)
        System.Console.WriteLine($"  [{candidate.Kind,-19}] {candidate.Executable}  ({candidate.Description})");

    var info = await runner.ProbeAsync();
    System.Console.WriteLine("the runner's configured interpreter reports:");
    System.Console.WriteLine($"  version        : {info.Version}");
    System.Console.WriteLine($"  executable     : {info.Executable}");
    System.Console.WriteLine($"  implementation : {info.Implementation}");
}

// -------------------------------------------------------------------------------------
// DEMO 7 — await foreach streaming: each PythonOutputLine arrives the moment Python
//          prints it, tagged with its source stream and arrival time. The growing
//          timestamps prove the lines were NOT buffered until exit.
// -------------------------------------------------------------------------------------
Header("DEMO 7 — await foreach over live output (StreamCodeAsync)");
{
    const string progressCode =
        "import sys, time\n" +
        "for i in range(1, 4):\n" +
        "    print(f'progress {i}/3', flush=True)\n" +
        "    time.sleep(0.2)\n" +
        "print('a warning mid-run', file=sys.stderr, flush=True)\n" +
        "time.sleep(0.2)\n" +
        "print('done', flush=True)\n";

    await foreach (var line in runner.StreamCodeAsync(progressCode))
        System.Console.WriteLine($"  [{line.Elapsed.TotalSeconds,5:F2}s {(line.IsError ? "stderr" : "stdout")}] {line.Text}");
}

// -------------------------------------------------------------------------------------
// DEMO 8 — two-way interactive: stdin stays open, so we can send a line, read the reply,
//          and repeat — streaming input AND output with one live process.
// -------------------------------------------------------------------------------------
Header("DEMO 8 — two-way interactive conversation (StartInteractive)");
{
    const string echoLoop =
        "import sys\n" +
        "for line in iter(sys.stdin.readline, ''):\n" +
        "    print(f\"py saw: {line.strip().upper()}\", flush=True)\n";

    await using var run = runner.StartInteractive(["-c", echoLoop]);

    string[] messages = ["hello", "streaming input", "and output"];
    foreach (var message in messages)
    {
        await run.WriteInputLineAsync(message);
        var reply = await run.ReadOutputLineAsync();
        System.Console.WriteLine($"  sent: {message,-16} -> got: {reply?.Text}");
    }

    run.CompleteInput(); // EOF ends the Python loop
    var result = await run.Completion;
    System.Console.WriteLine($"exit code    : {result.ExitCode} (clean shutdown on EOF)");
}

// -------------------------------------------------------------------------------------
// DEMO 9 — warm session: python/worker.py pays startup + imports ONCE, then answers many
//          requests over one line of stdin / one line of stdout each. Compare the per-call
//          cost with what a fresh `python -c` process costs on this machine.
// -------------------------------------------------------------------------------------
Header("DEMO 9 — warm session against python/worker.py (StartSession)");
{
    var workerPath = LocateWorkerScript();
    System.Console.WriteLine($"worker script: {workerPath}");

    // Baseline first: one whole fresh interpreter launch, timed.
    var freshStopwatch = Stopwatch.StartNew();
    await runner.RunCodeAsync("print('pong')");
    freshStopwatch.Stop();

    await using var session = runner.StartSession(workerPath);
    session.StandardErrorLine += line => System.Console.WriteLine("  [worker:stderr] " + line);

    var readyLine = await session.WaitForReadyAsync("ready");
    System.Console.WriteLine($"worker ready : '{readyLine}'");

    var pong = await session.SendAsync("""{"op": "ping"}""");
    System.Console.WriteLine($"raw ping     : {pong}");

    var addReply = await session.SendJsonAsync(
        new AddRequest { A = 2, B = 40 },
        PyJsonContext.Default.AddRequest,
        PyJsonContext.Default.AddReply);
    System.Console.WriteLine($"typed add    : add(2, 40) -> {addReply.Result} (ok={addReply.Ok})");

    var upperReply = await session.SendJsonAsync(
        new UpperRequest { Text = "pybridge" },
        PyJsonContext.Default.UpperRequest,
        PyJsonContext.Default.UpperReply);
    System.Console.WriteLine($"typed upper  : upper('pybridge') -> '{upperReply.Result}' (ok={upperReply.Ok})");

    const int burstSize = 5;
    var burstStopwatch = Stopwatch.StartNew();
    for (var i = 0; i < burstSize; i++)
        await session.SendAsync("""{"op": "ping"}""");
    burstStopwatch.Stop();

    var perCallMs = burstStopwatch.Elapsed.TotalMilliseconds / burstSize;
    System.Console.WriteLine($"burst        : {burstSize} ping round-trips in {burstStopwatch.Elapsed.TotalMilliseconds:F1} ms " +
                             $"= {perCallMs:F2} ms/call");
    System.Console.WriteLine($"vs fresh     : one fresh 'python -c' process cost {freshStopwatch.Elapsed.TotalMilliseconds:F0} ms — " +
                             "pay startup once, then calls are near-free");
}

// -------------------------------------------------------------------------------------
// DEMO 10 — embedded runtime: EnsureAsync() can bootstrap a private python.org runtime
//           for machines with no Python at all. Opt-in via env var — no surprise downloads.
// -------------------------------------------------------------------------------------
Header("DEMO 10 — private embedded runtime (EmbeddedPython.EnsureAsync)");
{
    if (Environment.GetEnvironmentVariable("PYBRIDGE_DEMO_EMBEDDED") == "1")
    {
        var embeddedOptions = await EmbeddedPython.EnsureAsync();
        var embeddedRunner = new PythonRunner(embeddedOptions);
        var result = await embeddedRunner.RunCodeAsync("import sys; print('hello from embedded'); print(sys.version.split()[0])");

        System.Console.WriteLine($"interpreter  : {embeddedOptions.PythonExecutable}");
        System.Console.WriteLine($"exit code    : {result.ExitCode}");
        System.Console.WriteLine("captured stdout:");
        PrintIndented(result.StandardOutput);
    }
    else
    {
        System.Console.WriteLine(
            "Skipped (no surprise downloads). Set PYBRIDGE_DEMO_EMBEDDED=1 and re-run to have " +
            "EmbeddedPython.EnsureAsync() install a private ~11 MB python.org runtime (once, per-user) " +
            "and run code through it.");
    }
}

Header("Done");
System.Console.WriteLine(torchAvailable
    ? "PyTorch is installed — DEMO 3 ran a real ResNet inference above."
    : "Everything worked. Install PyTorch (see python/README.md) and re-run to see DEMO 3\n" +
      "produce real ResNet predictions — no C# changes required.");
return 0;


// ------------------------------------ helpers ----------------------------------------

// Prefer a CUDA-enabled conda env (demonstrates PythonRunnerOptions.ForConda), else PATH python.
static PythonRunner BuildRunner(out string note)
{
    var envName = Environment.GetEnvironmentVariable("PYBRIDGE_CONDA_ENV");
    if (string.IsNullOrWhiteSpace(envName))
        envName = "pybridge";

    try
    {
        var options = PythonRunnerOptions.ForConda(envName);
        if (File.Exists(options.PythonExecutable))
        {
            note = $"conda env '{envName}' — activation PATH applied so CUDA/native DLLs load";
            return new PythonRunner(options);
        }
    }
    catch (PythonExecutionException)
    {
        // No conda installation found — fall through to plain `python`.
    }

    note = "default 'python' resolved from PATH";
    return new PythonRunner();
}

static void Header(string title)
{
    System.Console.WriteLine();
    System.Console.WriteLine(new string('=', 78));
    System.Console.WriteLine("  " + title);
    System.Console.WriteLine(new string('=', 78));
}

static void PrintIndented(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        System.Console.WriteLine("    (empty)");
        return;
    }

    foreach (var line in text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        System.Console.WriteLine("    | " + line);
}

// Walk up from the executable location and the current directory to find python/classifier.py.
static string LocateClassifierScript()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "python", "classifier.py");
            if (File.Exists(candidate))
                return candidate;
        }
    }

    throw new FileNotFoundException(
        "Could not find python/classifier.py by walking up from the executable or working directory. " +
        "Run this from within the repository.");
}

// Walk up from the executable location and the current directory to find python/worker.py.
static string LocateWorkerScript()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "python", "worker.py");
            if (File.Exists(candidate))
                return candidate;
        }
    }

    throw new FileNotFoundException(
        "Could not find python/worker.py by walking up from the executable or working directory. " +
        "Run this from within the repository.");
}
