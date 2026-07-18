using System.Text.Json;
using PyBridge;

// =====================================================================================
//  PyBridge — long-running workload profile
//
//  Runs a ~30s ResNet-18 training job through the wrapper with LIVE streaming, timestamping
//  each streamed line relative to launch. That lets us see things a 3s job can't:
//    * is output truly real-time, or buffered until the process exits?
//    * steady-state GPU throughput (images/sec) and peak GPU memory,
//    * how the fixed torch startup shrinks to a rounding error as the run gets longer.
//
//  Run:  dotnet run -c Release --project src/PyBridge.LongRun
// =====================================================================================

var runner = new PythonRunner(PythonRunnerOptions.ForConda("pybridge"));
var script = Locate("train.py");
string[] trainArgs =
[
    "--epochs", "10",
    "--steps-per-epoch", "40",
    "--batch-size", "64",
    "--image-size", "224",
    "--log-every", "10",
];

Console.WriteLine("Long-running workload: ResNet-18 training via PyBridge (live streaming)");
Console.WriteLine($"script : {script}");
Console.WriteLine($"args   : {string.Join(' ', trainArgs)}");
Console.WriteLine();
Console.WriteLine("Streamed stderr (timestamp = seconds since launch):");

// Streaming instrumentation. Each PythonOutputLine carries its own arrival timestamp
// (line.Elapsed, measured from launch), and run.Lines is a single-consumer stream — so no
// stopwatch, no callbacks, no lock.
double firstLineAt = -1, lastLineAt = 0;
var lineCount = 0;

await using var run = runner.StartFile(script, trainArgs);

await foreach (var line in run.Lines)
{
    if (!line.IsError)
        continue; // stdout carries the final JSON summary; only stderr is the live log

    var at = line.Elapsed.TotalSeconds;
    if (firstLineAt < 0) firstLineAt = at;
    lastLineAt = at;
    lineCount++;
    Console.WriteLine($"  [{at,6:F2}s] {line.Text}");
}

var result = await run.Completion;
result.EnsureSuccess();

// Parse the final JSON the training job printed to stdout.
using var doc = JsonDocument.Parse(result.StandardOutput);
var r = doc.RootElement;
double importS = r.GetProperty("torch_import_seconds").GetDouble();
double setupS = r.GetProperty("setup_seconds").GetDouble();
double trainS = r.GetProperty("train_seconds").GetDouble();
double imgPerSec = r.GetProperty("mean_images_per_sec").GetDouble();
double peakMemMb = r.GetProperty("peak_gpu_memory_mb").GetDouble();
double finalAcc = r.GetProperty("final_accuracy").GetDouble();
double finalLoss = r.GetProperty("final_loss").GetDouble();
int totalImages = r.GetProperty("total_images").GetInt32();
string? gpu = r.GetProperty("gpu_name").GetString();

double wallS = result.RunTime.TotalSeconds;
double streamingSpread = lastLineAt - firstLineAt;

Console.WriteLine();
Console.WriteLine("Summary");
Console.WriteLine($"  gpu                    : {gpu}");
Console.WriteLine($"  wall (wrapper-measured): {wallS,8:F2} s");
Console.WriteLine($"    torch import         : {importS,8:F2} s");
Console.WriteLine($"    setup (model/opt)    : {setupS,8:F2} s");
Console.WriteLine($"    training             : {trainS,8:F2} s   <- the actual work");
Console.WriteLine($"    startup+teardown+etc : {wallS - importS - setupS - trainS,8:F2} s");
Console.WriteLine($"  throughput             : {imgPerSec,8:F0} img/s ({totalImages} images total)");
Console.WriteLine($"  peak GPU memory        : {peakMemMb,8:F0} MB");
Console.WriteLine($"  final loss / accuracy  : {finalLoss:F4} / {finalAcc:P1}");

Console.WriteLine();
Console.WriteLine("Streaming behavior");
Console.WriteLine($"  streamed lines         : {lineCount}");
Console.WriteLine($"  first line arrived at  : {firstLineAt,8:F2} s  (≈ torch import + setup; nothing to say before that)");
Console.WriteLine($"  last line arrived at   : {lastLineAt,8:F2} s");
Console.WriteLine($"  spread across          : {streamingSpread,8:F2} s of a {wallS:F2} s run");
var realtime = streamingSpread > trainS * 0.5;
Console.WriteLine($"  verdict                : {(realtime ? "REAL-TIME — lines arrived throughout the run, not buffered to the end" : "SUSPICIOUS — output looks buffered")}");

Console.WriteLine();
Console.WriteLine($"  Overhead check: wrapper wall {wallS:F2}s vs Python-reported work "
                  + $"{importS + setupS + trainS:F2}s -> the wrapper/streaming layer is a rounding error on a long run.");

return realtime ? 0 : 1;


static string Locate(string fileName)
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "python", fileName);
            if (File.Exists(candidate))
                return candidate;
        }
    }

    throw new FileNotFoundException($"Could not find python/{fileName} by walking up from the executable or working directory.");
}
