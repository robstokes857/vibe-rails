using System.Diagnostics;
using System.Text.Json;
using PyBridge;

// =====================================================================================
//  PyBridge — performance / overhead benchmark
//
//  Question: what does calling Python *through C#* cost versus running it straight?
//
//  We measure wall-clock, with warmups, over several iterations:
//    1. A trivial `python -c "pass"` — isolates fixed per-call cost (interpreter start +
//       process launch + pipe plumbing). Measured two ways:
//         a) PyBridge wrapper (CliWrap)
//         b) a raw System.Diagnostics.Process  -> the delta is CliWrap's own overhead.
//    2. The full ResNet classify — with the Python side reporting its internal breakdown
//       (torch import / model load / warmed-up inference), so we can see exactly where the
//       seconds go.
//
//  Run:  dotnet run -c Release --project src/PyBridge.Bench
// =====================================================================================

const int classifyProcessRuns = 8;   // how many times we launch the classify process
const int classifyWarmups = 2;
const int passRuns = 25;
const int passWarmups = 5;
const int inferenceIters = 20;        // inference passes *inside* each classify process

var options = PythonRunnerOptions.ForConda("pybridge");
var runner = new PythonRunner(options);
var pythonExe = options.PythonExecutable;
var script = LocateClassifier();

Console.WriteLine("PyBridge overhead benchmark");
Console.WriteLine($"python : {pythonExe}");
Console.WriteLine($"script : {script}");
Console.WriteLine($"config : pass x{passRuns} (warmup {passWarmups}); classify x{classifyProcessRuns} " +
                  $"(warmup {classifyWarmups}), {inferenceIters} inference iters/process");

// --- 1. Trivial script: PyBridge wrapper vs raw .NET Process --------------------------
var passWrapper = await Measure("pass  — PyBridge (CliWrap)",
    () => runner.RunCodeAsync("pass"), passRuns, passWarmups);

var passRaw = await Measure("pass  — raw .NET Process",
    () => RunRawProcess(pythonExe, ["-c", "pass"]), passRuns, passWarmups);

// --- 2. Full ResNet classify through the wrapper (with Python-side breakdown) ----------
double importMs = 0, loadMs = 0, warmupMs = 0, inferenceMs = 0, lastWallMs = 0;
var wallSw = new Stopwatch();
var classifyWrapper = await Measure($"classify (iters={inferenceIters}) — PyBridge (CliWrap)", async () =>
{
    wallSw.Restart();
    var result = await runner.RunFileAsync(script, "classify", "--iters", inferenceIters.ToString());
    wallSw.Stop();
    result.EnsureSuccess();
    lastWallMs = wallSw.Elapsed.TotalMilliseconds;   // wall time of THIS run, to align with its breakdown
    using var doc = JsonDocument.Parse(result.StandardOutput);
    var root = doc.RootElement;
    importMs = root.GetProperty("torch_import_seconds").GetDouble() * 1000;
    loadMs = root.GetProperty("model_load_seconds").GetDouble() * 1000;
    warmupMs = root.GetProperty("warmup_seconds").GetDouble() * 1000;
    inferenceMs = root.GetProperty("inference_seconds").GetDouble() * 1000;
}, classifyProcessRuns, classifyWarmups);

// --- Report ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Results (milliseconds)");
Console.WriteLine($"  {"benchmark",-40} {"min",8} {"mean",8} {"median",8} {"max",8} {"stdev",8}");
Console.WriteLine("  " + new string('-', 92));
foreach (var s in new[] { passRaw, passWrapper, classifyWrapper })
    Console.WriteLine($"  {s.Label,-40} {s.Min,8:F1} {s.Mean,8:F1} {s.Median,8:F1} {s.Max,8:F1} {s.StdDev,8:F1}");

var cliWrapOverhead = passWrapper.Median - passRaw.Median;
Console.WriteLine();
Console.WriteLine("Interpretation");
Console.WriteLine($"  CliWrap overhead vs raw Process (trivial call) : {cliWrapOverhead:F1} ms/call (median delta)");
Console.WriteLine($"  Fixed per-call cost via wrapper (interp+launch): {passWrapper.Median:F1} ms (the 'pass' median)");
Console.WriteLine();
Console.WriteLine($"  classify total wall (via wrapper, median)      : {classifyWrapper.Median:F1} ms");
Console.WriteLine($"  breakdown of one aligned run (wall {lastWallMs:F1} ms):");
Console.WriteLine($"      import torch         : {importMs,8:F1} ms   (one-time per process)");
Console.WriteLine($"      load resnet + prep   : {loadMs,8:F1} ms   (one-time per process)");
Console.WriteLine($"      CUDA warmup (1st fwd): {warmupMs,8:F1} ms   (one-time per process)");
Console.WriteLine($"      inference (mean/ea)  : {inferenceMs,8:F2} ms   x{inferenceIters} warmed-up passes");
var pythonWork = importMs + loadMs + warmupMs + inferenceMs * inferenceIters;
Console.WriteLine($"      -> Python work total : {pythonWork,8:F1} ms");
Console.WriteLine($"      -> everything else   : {lastWallMs - pythonWork,8:F1} ms " +
                  "(interp start + CUDA teardown at exit + launch + C# wrapper)");
Console.WriteLine();
Console.WriteLine("  Bottom line: the C# wrapper adds a few ms; the cost is Python startup +");
Console.WriteLine("  torch import + model load, paid once per process. Warmed-up inference is ~ms.");

return 0;


// ------------------------------------ helpers ----------------------------------------

static async Task<Stats> Measure(string label, Func<Task> action, int iterations, int warmups)
{
    for (var i = 0; i < warmups; i++)
        await action();

    var samples = new double[iterations];
    var sw = new Stopwatch();
    for (var i = 0; i < iterations; i++)
    {
        sw.Restart();
        await action();
        sw.Stop();
        samples[i] = sw.Elapsed.TotalMilliseconds;
    }

    return new Stats(label, samples);
}

// A deliberately minimal, dependency-free process launch — the .NET floor to compare against.
static async Task RunRawProcess(string exe, string[] arguments)
{
    var psi = new ProcessStartInfo(exe)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var arg in arguments)
        psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await Task.WhenAll(stdout, stderr);
}

static string LocateClassifier()
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

    throw new FileNotFoundException("Could not find python/classifier.py by walking up from the executable or working directory.");
}

internal sealed class Stats
{
    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Mean { get; }
    public double Median { get; }
    public double StdDev { get; }

    public Stats(string label, double[] samples)
    {
        Label = label;
        Min = samples.Min();
        Max = samples.Max();
        Mean = samples.Average();

        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        Median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

        var variance = samples.Sum(x => (x - Mean) * (x - Mean)) / samples.Length;
        StdDev = Math.Sqrt(variance);
    }
}
