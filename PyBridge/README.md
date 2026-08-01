# PyBridge: call Python from C# without the pain

A small, **AOT-friendly** .NET library for running Python from C#: buffered results,
live streaming in **both directions** (stdout/stderr out, stdin in), warm long-lived
worker sessions, and zero-thought defaults. `PythonRunnerOptions.Discover()` finds the right
interpreter (venv → conda → PATH) so your code doesn't have to care. Built on
[CliWrap](https://github.com/Tyrrrz/CliWrap), which does the genuinely hard parts
(async piping without deadlocks, argument escaping, cancellation, process teardown).

> **The contract:** if a Python command runs when you type it at a terminal, it runs
> identically through PyBridge — and its input/output flows straight back to your C# code.

## What's in here

| Path | What it is |
|------|-----------|
| `src/PyBridge/` | The class library (the wrapper). AOT-compatible, zero reflection. |
| `src/PyBridge.Console/` | Demo app: stdin/stdout round-trips, JSON parsing, GPU inference, streaming, error handling. |
| `src/PyBridge.Tests/` | Failure-handling + API tests (bad exits, timeouts, kills, sessions, discovery). |
| `src/PyBridge.Bench/` | Overhead benchmark: wrapper cost vs raw process cost. |
| `src/PyBridge.LongRun/` | Long-run streaming profile: ~30s GPU training job streamed live with per-line timestamps. |
| `python/` | `classifier.py` (ResNet inference), `train.py` (ResNet-18 training), `worker.py` (reference session worker). |

## Quick start

```powershell
# from the repo root
dotnet run --project src/PyBridge.Console -c Release
```

Or, the one-liner (discovers the interpreter for you):

```csharp
var result = await PythonRunner.QuickRunAsync("script.py", "--flag", "x");
```

## Pick your shape

Four core shapes, from "just give me the result" to "full two-way conversation".

**1. Buffered** — run to completion, parse JSON output the AOT-safe way:

```csharp
var runner = new PythonRunner(PythonRunnerOptions.Discover());

var result = await runner.RunFileAsync("analyze.py", "--input", "data.csv");
result.EnsureSuccess();                                       // throws rich exception on failure

Report? report = result.ReadJsonOutput(MyContext.Default.Report);  // source-generated, no reflection

[JsonSerializable(typeof(Report))]
partial class MyContext : JsonSerializerContext;
```

**2. Streamed** — `await foreach` live output; each line knows when it arrived:

```csharp
await foreach (var line in runner.StreamFileAsync("train.py", "--epochs", "10"))
    Console.WriteLine($"[{line.Elapsed.TotalSeconds,6:F2}s] {(line.IsError ? "ERR" : "OUT")} {line.Text}");
// throws PythonExecutionException after the last line if the run failed
```

**3. Handle** — lines *and* the final result *and* kill, all on one object:

```csharp
await using var run = runner.StartFile("train.py", ["--epochs", "100"]);

await foreach (var line in run.Lines)
{
    Console.WriteLine(line);
    if (line.Text.Contains("loss=nan"))
        await run.KillAsync();                 // result will have WasKilled = true
}

var result = await run.Completion;             // exit code, full stdout/stderr, timing
```

**4. Interactive** — stdin stays open; write lines in while reading lines out:

```csharp
await using var run = runner.StartInteractiveFile("echo.py");

await run.WriteInputLineAsync("hello");
var reply = await run.ReadOutputLineAsync();   // -> "HELLO"

run.CompleteInput();                           // EOF: the script's stdin loop ends
var result = await run.Completion;
```

```python
# echo.py
import sys
for line in iter(sys.stdin.readline, ''):
    print(line.strip().upper(), flush=True)
```

**5. Session** — a warm worker: pay interpreter startup + imports once, then many cheap calls:

```csharp
await using var session = runner.StartSession("worker.py");
session.StandardErrorLine += line => Console.WriteLine($"[worker] {line}");

await session.WaitForReadyAsync("ready");      // waits out interpreter startup + heavy imports

var pong = await session.SendAsync("""{"op": "ping"}""");   // one line in, one line out

var sum = await session.SendJsonAsync(         // typed + AOT-safe via JsonTypeInfo
    new AddRequest(2, 40), Ctx.Default.AddRequest, Ctx.Default.AddReply);
```

The worker side is trivial Python (see `python/worker.py` for the full reference):

```python
import json, sys
print("ready", flush=True)                     # heavy imports/model load go BEFORE this
for line in iter(sys.stdin.readline, ''):
    req = json.loads(line)
    print(json.dumps({"ok": True, "result": req["a"] + req["b"]}), flush=True)
```

## Interfaces & DI

`IPythonRunner` has just **four core methods**: `RunAsync`, `StreamAsync`, `Start`,
`StartInteractive`. Everything else — `RunFileAsync`, `RunModuleAsync`, `RunCodeAsync`,
the `Stream*` family, `StartFile`, `StartInteractiveFile`, `StartSession`,
`StartSessionForModule`, `ProbeAsync` — is provided as **C# 14 extension members**, so any
implementation (including your test mock) gets the full convenience surface for free.

```csharp
services.AddSingleton<IPythonRunner>(new PythonRunner(PythonRunnerOptions.Discover()));
```

Runners are cheap and hold no per-run state — every method is safe to call concurrently
(just don't mutate `Options` mid-flight). One per interpreter/environment, reused forever.

## Finding Python

`PythonRunnerOptions.Discover()` (backed by `PythonLocator`) checks, in priority order:

1. **`PYBRIDGE_PYTHON`** environment variable — explicit user override.
2. The **active virtual environment** (`VIRTUAL_ENV`).
3. A **local `.venv`/`venv`** directory, walking up from the working directory (like git).
4. **conda** — base environment, then every named environment.
5. **PATH** — with the fake Microsoft Store `python.exe` aliases skipped.
6. The Windows **`py` launcher**.

Or be explicit — each factory reproduces the activation the environment needs
(PATH entries, `VIRTUAL_ENV`/`CONDA_PREFIX`), so native wheels and CUDA DLLs resolve:

```csharp
PythonRunnerOptions.ForPython(@"C:\Python313\python.exe");
PythonRunnerOptions.ForVenv("./.venv");
PythonRunnerOptions.ForConda("pybridge");          // by env name
PythonRunnerOptions.ForCondaPrefix(@"C:\miniconda3\envs\pybridge");
```

Health-check with `await runner.ProbeAsync()` — returns the interpreter's actual
version/executable/prefix/implementation, not the one you hoped was on PATH.

## Should you bundle Python?

- **Your users have Python** (dev tools, data teams): `Discover()` is enough. Ship nothing.
- **Your users don't**: call `EmbeddedPython.EnsureAsync()` at startup. It bootstraps the
  official python.org *Windows embeddable package* (~11 MB download, first run only) into
  `%LOCALAPPDATA%\PyBridge\runtimes\...` — per-user, no admin rights, shared across your apps —
  and returns ready-to-use `PythonRunnerOptions`. Every later call is just a couple of file checks.

```csharp
var options = await EmbeddedPython.EnsureAsync(new EmbeddedPythonOptions
{
    Version = "3.13.5",
    EnsurePip = true,                        // off by default; bare runtime runs dependency-free scripts
    ExpectedZipSha256 = "…",                 // pin the download in production
});
var runner = new PythonRunner(options);
```

This beats shipping an interpreter *inside* your package: your installer stays small, the
runtime can be patched/re-versioned without re-shipping your app, and licensing hygiene is
simpler (the bits come straight from python.org, verifiable via `ExpectedZipSha256`).

**Windows-only** — python.org publishes embeddable builds only for Windows. On Linux/macOS
use the system interpreter, a venv, or conda; `Discover()` finds them.

## Streaming details

- Every streamed line is a `PythonOutputLine { Source, Text, Elapsed }` — which stream it
  came from, the text (implicitly converts to `string`), and how long after launch it arrived.
- `UseUnbufferedOutput` (default **on**) sets `PYTHONUNBUFFERED=1` so lines arrive in real
  time instead of when Python's buffer happens to flush.
- `UseUtf8Io` (default **on**) sets `PYTHONIOENCODING=utf-8` + `PYTHONUTF8=1` — without it,
  Python on Windows encodes piped output with the legacy locale code page and non-ASCII
  text arrives mangled.
- **`StreamAsync` throws** (`PythonExecutionException`, after the last line) when the run
  fails or times out, because there's no result object to inspect. **`Start` never does** —
  await `run.Completion` and inspect `ExitCode` / `TimedOut` / `WasKilled` yourself.

## Sessions

`PythonSession` speaks a dead-simple protocol: **one line of stdin per request, one line of
stdout per reply**.

- **stderr never corrupts replies** — worker logs/warnings/tracebacks are routed to the
  `StandardErrorLine` event as replies are read (stderr between requests surfaces on the next
  call; everything is always captured in `Completion`'s result), so Python can log freely.
- **Cancelling a request mid-flight poisons the session** (`IsFaulted`) — the reply it
  abandoned would misalign every later call, so the session fails fast instead of silently
  returning wrong data. Dispose and start a fresh one.
- `WaitForReadyAsync("ready")` blocks until the worker announces itself — put heavy imports
  and model loading *before* the ready line and the wait covers them exactly.
- `SendJsonAsync` is AOT-safe: you pass source-generated `JsonTypeInfo<T>` for request and
  reply, so no runtime reflection anywhere.
- Requests are serialized internally — a session is safe to share between concurrent callers.
- `DisposeAsync()` sends EOF (a well-behaved worker exits cleanly), waits up to 5 s, then
  kills. It never throws; await `Completion` first if you want to assert a clean exit.

## .NET 10 / AOT notes

- The library sets `IsAotCompatible` / `IsTrimmable` and the console sets `PublishAot`, with
  the trim + AOT analyzers enabled. **The whole solution builds with zero AOT/trim warnings**,
  so the code path is AOT-clean (including the CliWrap usage).
- `dotnet run` / `dotnet build` work as normal JIT builds. Producing an actual native binary
  with `dotnet publish -c Release` requires the **MSVC C++ toolchain** on Windows (Visual
  Studio → "Desktop development with C++"). That toolchain is not currently installed on this
  machine, so the native publish step is the one thing not exercised here — the AOT *analyzers*
  confirm the code itself is ready.
- Modern .NET touches worth noting:
  - **C# 14 extension members** keep `IPythonRunner` at four methods while every implementation gets the full surface.
  - **`params ReadOnlySpan<string>` overloads** make the common call allocation-light: `runner.RunFileAsync("train.py", "--epochs", "10")`.
  - **`System.Threading.Channels`** plumbs live output and live stdin between the process pipes and your `await foreach`.
  - **`SearchValues`** for fast argument-display quoting checks.
  - **Source-generated JSON end-to-end** — `ProbeAsync`, `ReadJsonOutput<T>`, and `SendJsonAsync` all take compile-time `JsonTypeInfo`, never reflection.

## Python setup (to light up the GPU demos)

See [`python/README.md`](python/README.md). Short version:

```powershell
# conda (GPU):
conda env create -f python/environment.yml
conda activate pybridge

# or pip for a newer GPU (RTX 5060 Ti / Blackwell needs CUDA 12.8+ wheels):
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128
```

Then re-run the console app — the inference demo prints real ImageNet predictions,
**no C# changes required**.
