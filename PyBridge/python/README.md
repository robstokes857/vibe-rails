# Python side — PyTorch workloads + session worker

This folder holds the "real" Python workloads the .NET wrapper drives:

| Script | What it is |
|--------|-----------|
| `classifier.py` | One-shot ResNet image classifier (PyTorch/torchvision, CUDA when available). |
| `train.py` | Sustained GPU workload: trains a ResNet from scratch on synthetic-but-learnable data, streaming per-step/per-epoch progress. Used by the `PyBridge.LongRun` profile. |
| `worker.py` | Reference `PythonSession` worker — dependency-free, line-based JSON over stdio (one line in, one line out). |
| `tests/` | Small failure fixtures (`fail_missing_lib.py`, `fail_runtime.py`) exercised by the C# test project. |

They're plain command-line programs. The rule the whole POC is built around:

> If a script runs when you type it at a terminal, it runs identically when launched
> by the .NET `PyBridge` wrapper — with stdin, stdout, stderr, and the exit code all captured
> and handed back to the C# caller.

## Contract (`classifier.py` / `train.py`)

- **stdout** = machine-readable JSON only (the result).
- **stderr** = human-readable progress/log lines.
- **exit code**: `0` success · `2` bad usage · `3` missing dependency (e.g. no PyTorch) · `4` runtime failure.

## Commands (`classifier.py`)

```bash
# Environment report — standard library only, so it ALWAYS runs (even with no PyTorch):
python classifier.py check

# Classify an image with a pretrained ResNet (needs torch + torchvision):
python classifier.py classify --image path/to/photo.jpg --topk 5

# Choose a ResNet variant and run timed inference passes for profiling:
python classifier.py classify --model resnet50 --iters 10

# No image? A synthetic test image is generated so the pipeline still runs end to end:
python classifier.py classify
```

First `classify` run downloads the pretrained ImageNet weights (~tens of MB) into the
torch hub cache; subsequent runs are offline.

## `train.py`

A sustained GPU workload that trains a ResNet from scratch on synthetic-but-learnable data
(each image's label comes from a fixed random linear "teacher", so the loss genuinely goes
down). Same JSON contract as `classifier.py`; streams per-step and per-epoch progress to
stderr. No dataset download — fully deterministic given `--seed`.

```bash
# Defaults: resnet18, 10 epochs, 50 steps/epoch, batch 64 (~30s on a modest GPU):
python train.py

# Bigger model, longer run:
python train.py --model resnet50 --epochs 20 --steps-per-epoch 100 --batch-size 128

# CPU-only smoke test (small image, few steps):
python train.py --image-size 64 --steps-per-epoch 5 --epochs 2
```

Flags: `--model` (resnet18/resnet50), `--epochs`, `--steps-per-epoch`, `--batch-size`,
`--image-size`, `--num-classes`, `--lr`, `--log-every`, `--seed`. Result JSON includes
per-epoch loss/accuracy/timing, mean images/sec, and peak GPU memory.

## Installing PyTorch

### Option A — conda (recommended, GPU)

```bash
conda env create -f environment.yml
conda activate pybridge
python classifier.py check     # look for "cuda_available": true
```

### Option B — pip + CUDA index (for newer GPUs like the RTX 5060 Ti / Blackwell)

Blackwell cards (sm_120) need CUDA 12.8+ wheels:

```bash
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128
pip install pillow
```

### Option C — pip, CPU only (quickest, no GPU)

```bash
pip install -r requirements.txt   # CPU wheels from PyPI
```

## `worker.py`

The reference `PythonSession` worker. **No PyTorch, no dependencies** — it's pure-stdlib on
purpose. It speaks the session protocol: print a `ready` line once startup is done, then loop
reading one JSON request line from stdin and writing one JSON reply line to stdout (flushed),
until EOF.

```bash
# Talk to it by hand (type a JSON request, press Enter, get a JSON reply):
python worker.py
```

Supported ops: `ping` → `"pong"`, `upper` (text) → uppercased string, `add` (a, b) → sum,
`sleep` (seconds) → simulated slow work. Unknown ops return `{"ok": false, "error": ...}`.
stderr is free for logging and never corrupts replies. A real worker would do its heavy
lifting (import torch, load a model) *before* the `ready` line so the C# side's
`WaitForReadyAsync("ready")` covers it.

## Driving it from .NET

The `PyBridge.Console` app in this repo drives `classifier.py`, `train.py`, and `worker.py`
through the `PyBridge` library and prints what it captured. See the repo root `README.md`.

---

**Last checked**: 2026-08-05T14:24:43Z by opencode (glm-5.2)
