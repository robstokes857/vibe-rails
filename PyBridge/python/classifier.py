#!/usr/bin/env python
"""ResNet image classifier -- the "PyTorch + CUDA + conda" payload for the PyBridge POC.

This is a perfectly ordinary command-line Python program. If it runs when you type it at a
terminal, it runs identically when launched by the .NET PyBridge wrapper -- that is the whole
point of the exercise. All human-readable logging goes to STDERR; anything printed to STDOUT
is machine-readable JSON, so the .NET side can parse it cleanly.

Sub-commands
------------
  python classifier.py check
      Print a JSON report about the Python / Torch / CUDA environment. Uses only the standard
      library, so it ALWAYS runs -- even before you have installed PyTorch. Great first smoke
      test for the .NET wrapper.

  python classifier.py classify [--image PATH] [--model resnet18|resnet50] [--topk N]
      Load a pretrained ResNet and classify an image, printing the top-K ImageNet predictions
      as JSON. Requires torch + torchvision (see requirements.txt / environment.yml). If
      --image is omitted, a synthetic test image is generated so the full pipeline can be
      exercised without supplying a file.

Exit codes: 0 = success, 2 = bad usage, 3 = missing dependency, 4 = runtime failure.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import platform
import sys
import time


def log(message: str) -> None:
    """Write a diagnostic line to stderr (keeps stdout pure JSON)."""
    print(message, file=sys.stderr, flush=True)


def emit(payload: dict) -> None:
    """Write the machine-readable JSON result to stdout."""
    print(json.dumps(payload, indent=2), flush=True)


def cmd_check(_args: argparse.Namespace) -> int:
    """Report the environment. Deliberately dependency-free so it always succeeds."""
    report = {
        "ok": True,
        "python_version": platform.python_version(),
        "python_executable": sys.executable,
        "platform": platform.platform(),
        "torch_available": False,
        "torchvision_available": False,
    }

    try:
        import torch  # noqa: PLC0415  (import inside function is intentional)

        report["torch_available"] = True
        report["torch_version"] = torch.__version__
        report["cuda_available"] = bool(torch.cuda.is_available())
        if torch.cuda.is_available():
            report["cuda_version"] = torch.version.cuda
            report["gpu_count"] = torch.cuda.device_count()
            report["gpu_name"] = torch.cuda.get_device_name(0)
    except ImportError as exc:
        report["torch_import_error"] = str(exc)

    try:
        import torchvision  # noqa: PLC0415

        report["torchvision_available"] = True
        report["torchvision_version"] = torchvision.__version__
    except ImportError as exc:
        report["torchvision_import_error"] = str(exc)

    log("[check] environment report generated")
    emit(report)
    return 0


def _load_model(model_name: str):
    """Return (model, weights) for a supported torchvision ResNet."""
    from torchvision import models  # noqa: PLC0415

    factories = {
        "resnet18": (models.resnet18, models.ResNet18_Weights),
        "resnet50": (models.resnet50, models.ResNet50_Weights),
    }
    if model_name not in factories:
        raise ValueError(f"Unsupported model '{model_name}'. Choose one of: {', '.join(factories)}")

    factory, weights_enum = factories[model_name]
    weights = weights_enum.DEFAULT
    log(f"[classify] loading {model_name} with pretrained ImageNet weights "
        f"(first run downloads ~tens of MB to the torch hub cache)...")
    model = factory(weights=weights)
    model.eval()
    return model, weights


def _load_image(image_path: str | None):
    """Open the given image, or synthesize a deterministic test image if none was provided."""
    from PIL import Image  # noqa: PLC0415

    if image_path:
        log(f"[classify] loading image from {image_path}")
        return Image.open(image_path).convert("RGB"), False

    # No image supplied: build a simple synthetic RGB gradient so the pipeline still runs.
    log("[classify] no --image given; generating a synthetic 256x256 test image")
    width = height = 256
    image = Image.new("RGB", (width, height))
    pixels = image.load()
    for y in range(height):
        for x in range(width):
            pixels[x, y] = (x % 256, y % 256, (x + y) % 256)
    return image, True


def cmd_classify(args: argparse.Namespace) -> int:
    import_started = time.perf_counter()
    try:
        import torch  # noqa: PLC0415
    except ImportError as exc:
        log("[classify] ERROR: PyTorch is not installed in this environment.")
        log("           Install it, e.g.:  conda env create -f environment.yml")
        log("           or:                pip install -r requirements.txt")
        log(f"           (import error: {exc})")
        return 3
    torch_import_seconds = time.perf_counter() - import_started

    try:
        # Keep stdout pristine so the .NET side always parses clean JSON: any chatter that
        # torch / torchvision prints to stdout (e.g. the pretrained-weights "Downloading..."
        # line on first run) is redirected to stderr for the duration of the work.
        with contextlib.redirect_stdout(sys.stderr):
            load_started = time.perf_counter()
            model, weights = _load_model(args.model)
            image, synthetic = _load_image(args.image)

            device = "cuda" if torch.cuda.is_available() else "cpu"
            log(f"[classify] using device: {device}")
            model = model.to(device)

            preprocess = weights.transforms()
            batch = preprocess(image).unsqueeze(0).to(device)
            model_load_seconds = time.perf_counter() - load_started

            iters = max(1, args.iters)

            # Warm up once: the first forward pass on CUDA also builds kernels / initializes
            # the context, which we don't want counted as steady-state inference time. We DO
            # measure it separately, because it is a real one-time-per-process cost.
            warmup_started = time.perf_counter()
            with torch.no_grad():
                logits = model(batch)
            if device == "cuda":
                torch.cuda.synchronize()
            warmup_seconds = time.perf_counter() - warmup_started

            timings = []
            for _ in range(iters):
                iter_started = time.perf_counter()
                with torch.no_grad():
                    logits = model(batch)
                if device == "cuda":
                    torch.cuda.synchronize()  # CUDA is async; sync for an honest measurement
                timings.append(time.perf_counter() - iter_started)

            mean_inference = sum(timings) / len(timings)
            probabilities = torch.nn.functional.softmax(logits[0], dim=0)

            categories = weights.meta["categories"]
            topk = min(args.topk, len(categories))
            confidences, indices = torch.topk(probabilities, topk)

            predictions = [
                {
                    "rank": rank + 1,
                    "label": categories[int(idx)],
                    "class_index": int(idx),
                    "confidence": round(float(conf), 6),
                }
                for rank, (conf, idx) in enumerate(zip(confidences, indices))
            ]

            result = {
                "ok": True,
                "model": args.model,
                "device": device,
                "cuda_available": bool(torch.cuda.is_available()),
                "synthetic_image": synthetic,
                "image": args.image,
                "torch_import_seconds": round(torch_import_seconds, 6),
                "model_load_seconds": round(model_load_seconds, 6),
                "warmup_seconds": round(warmup_seconds, 6),
                "inference_seconds": round(mean_inference, 6),
                "inference_iters": iters,
                "predictions": predictions,
            }
            log(f"[classify] {iters} inference iter(s), mean {mean_inference * 1000:.2f} ms; "
                f"top label: {predictions[0]['label']}")

        # Redirect is now unwound: emit the JSON result to the real stdout.
        emit(result)
        return 0
    except Exception as exc:  # noqa: BLE001  (top-level CLI guard)
        log(f"[classify] ERROR: {type(exc).__name__}: {exc}")
        return 4


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="ResNet image classifier for the PyBridge POC.")
    sub = parser.add_subparsers(dest="command", required=True)

    check = sub.add_parser("check", help="Report the Python/Torch/CUDA environment as JSON.")
    check.set_defaults(func=cmd_check)

    classify = sub.add_parser("classify", help="Classify an image with a pretrained ResNet.")
    classify.add_argument("--image", help="Path to an image file. Omit to use a synthetic test image.")
    classify.add_argument("--model", default="resnet18", choices=["resnet18", "resnet50"],
                          help="Which ResNet variant to use (default: resnet18).")
    classify.add_argument("--topk", type=int, default=5, help="How many top predictions to return (default: 5).")
    classify.add_argument("--iters", type=int, default=1,
                          help="Timed inference passes to run after a warmup (for profiling). Default: 1.")
    classify.set_defaults(func=cmd_classify)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
