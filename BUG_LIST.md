# Bug List

This file tracks active bugs, investigations, and follow-up work for VibeRails.

## BUG-001: BERT Backfill Crashes VS Code Terminal Tab Child Process

Status: Mitigated in working tree, needs verification and final root-cause fix

Severity: High

Area: BERT V2, hosted jobs, VS Code extension, terminal tabs, ONNX Runtime

First observed: 2026-04-24

### Summary

When VibeRails is launched through the VS Code extension, Web UI terminal tab child processes can die around 5 minutes after startup. The timing lines up with the first `BertEmbeddingBackfillJob` tick. The failure is not in xterm, terminal proxying, MCP, or the LLM CLI process. The crash happens when the BERT job initializes ONNX Runtime inside a terminal-tab child process.

### User-visible symptoms

- App feels slow or unstable when launched through the VS Code extension.
- Web UI terminal tab disconnects or dies after roughly 5 minutes.
- Parent backend reports the child process exited unexpectedly.
- The failure is worse in VS Code because VS Code mode creates extra backend child processes for terminal tabs.

### Evidence

Root backend log showed the BERT job starting and then ONNX Runtime failing during native API initialization:

```text
[Job:BertEmbeddingBackfillJob] Tick begin. tick=1
[BertEmbedder] Constructing. modelPath=C:\Users\robst\.vibe_rails\models\bertv2\model.onnx
System.TypeInitializationException
 ---> System.ArgumentNullException: Value cannot be null. (Parameter 'ptr')
   at System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(...)
   at Microsoft.ML.OnnxRuntime.CompileApi.NativeMethods..ctor(...)
   at Microsoft.ML.OnnxRuntime.NativeMethods..cctor()
   at Microsoft.ML.OnnxRuntime.SessionOptions..ctor()
```

The terminal-tab child process died at the same time:

```text
[TerminalTabs] Child process exited.
exitCode=-1073741819
exitCodeHex=0xC0000005
uptime="00:05:00..."
```

`0xC0000005` is a native access violation.

### What is failing

The failure occurs before the model is loaded and before any embedding is generated. It happens while constructing:

```csharp
new Microsoft.ML.OnnxRuntime.SessionOptions()
```

That triggers `Microsoft.ML.OnnxRuntime.NativeMethods` static initialization. ONNX Runtime loads `onnxruntime.dll` and binds native API function pointers. The stack points to the ONNX `CompileApi` binding path. The managed wrapper tries to convert a native function pointer into a delegate, but the pointer is null.

This suggests a native ONNX Runtime binding or packaging issue, not a corrupt BERT model. Other logs show the same model can load and run successfully in other process instances.

### Why VS Code makes it worse

In VS Code mode, VibeRails starts:

- one root backend process
- one child backend process per Web UI terminal tab

The terminal-tab child processes were registering global hosted maintenance jobs, including `BertEmbeddingBackfillJob`. Those child processes should only host terminal tab endpoints and should not run global background work. Running BERT backfill in every child multiplies ONNX initialization and lets a child process crash independently of the root backend.

### Suspected root causes

1. Terminal tab child processes incorrectly register global maintenance hosted services.
2. BERT backfill eagerly resolves `IBertV2InputService`, which constructs the ONNX-backed embedder even when no rows need embedding.
3. The VS Code packaged backend may not ship `onnxruntime.dll` side-by-side with `vb.exe`; Windows is finding ONNX through `PATH` at `C:\Users\robst\.vibe_rails`.
4. ONNX Runtime 1.24.4 introduces or initializes the `CompileApi` table. The observed failure is a null function pointer in that API binding path.

### Current mitigation in working tree

The current local changes mitigate the crash path:

- `MapRegisterServices.Register(...)` now receives launch args.
- Processes with `--parent-pid` are treated as terminal-tab children.
- Terminal-tab children skip global maintenance jobs:
  - `UpdateCheckJob`
  - `StaleSessionCleanupJob`
  - `ProjectCacheRefreshJob`
  - `CleanedUserInputBackfillJob`
  - `BertEmbeddingBackfillJob`
- `BertEmbeddingBackfillJob` checks for pending unembedded rows before resolving the ONNX-backed input service.
- `BertEmbeddingBackfillJob` is low priority and smaller batch size.
- If ONNX-backed embedder initialization fails, the BERT backfill disables itself for that process instead of retrying every 5 minutes.

### Remaining follow-up work

- Verify VS Code extension terminal tabs stay alive past 5 minutes.
- Run the full test suite after the sandbox/tool-cache issue is cleared.
- Decide whether BERT should be behind an explicit config flag for production builds.
- Ensure packaged VS Code extension includes the required ONNX Runtime native files side-by-side, or remove ONNX from the extension-hosted process path.
- Consider isolating BERT embedding into a single-process worker or queued service owned only by the root backend.
- Evaluate pinning/downgrading ONNX Runtime if the `CompileApi` null pointer issue persists with correct native packaging.
- Add a regression test around hosted service registration for `--parent-pid` child processes.

### Verification so far

- `dotnet build VibeRails\VibeRails.csproj` passed.
- Full `dotnet test` was not completed during investigation.

