# Python side — ResNet image classifier

This folder is the "real" Python workload the .NET wrapper drives: a PyTorch/torchvision
ResNet image classifier that runs on CUDA when a compatible GPU is available.

It's a plain command-line program. The rule the whole POC is built around:

> If `classifier.py` runs when you type it at a terminal, it runs identically when launched
> by the .NET `PyBridge` wrapper — with stdin, stdout, stderr, and the exit code all captured
> and handed back to the C# caller.

## Contract

- **stdout** = machine-readable JSON only (the result).
- **stderr** = human-readable progress/log lines.
- **exit code**: `0` success · `2` bad usage · `3` missing dependency (e.g. no PyTorch) · `4` runtime failure.

## Commands

```bash
# Environment report — standard library only, so it ALWAYS runs (even with no PyTorch):
python classifier.py check

# Classify an image with a pretrained ResNet (needs torch + torchvision):
python classifier.py classify --image path/to/photo.jpg --topk 5

# No image? A synthetic test image is generated so the pipeline still runs end to end:
python classifier.py classify
```

First `classify` run downloads the pretrained ImageNet weights (~tens of MB) into the
torch hub cache; subsequent runs are offline.

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

## Driving it from .NET

The `PyBridge.Console` app in this repo calls every one of the commands above through the
`PyBridge` library and prints what it captured. See the repo root `README.md`.
